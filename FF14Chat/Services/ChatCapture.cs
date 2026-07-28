using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FF14Chat.Model;
using FF14Chat.Services.Translation;

namespace FF14Chat.Services;

/// <summary>Subscribes to the game's chat events and feeds the message store.</summary>
public sealed class ChatCapture : IDisposable
{
    private readonly MessageStore store;
    private readonly TabManager tabs;
    private readonly MessageDatabase database;
    private readonly PresenceTracker presence;
    private readonly TranslationService translation;

    private XivChatType lastType;
    private string lastSender = string.Empty;
    private string lastText = string.Empty;
    private DateTime lastTimestamp;

    // The game echoes an outgoing tell into the log even when delivery
    // fails, then follows with an error line ("...could not be delivered").
    // Remember the echo so that error can be routed into the same tell tab.
    private string? lastOutgoingTellPartner;
    private DateTime lastOutgoingTellAt;

    // Diagnostic window: log every event for the first minute after load.
    private readonly DateTime loadedAt = DateTime.Now;

    public ChatCapture(
        MessageStore store,
        TabManager tabs,
        MessageDatabase database,
        PresenceTracker presence,
        TranslationService translation)
    {
        this.store = store;
        this.tabs = tabs;
        this.database = database;
        this.presence = presence;
        this.translation = translation;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        // The chat event can fire more than once for a single message (e.g.
        // one per vanilla panel displaying it); drop exact repeats arriving
        // within a few hundred milliseconds.
        var now = DateTime.Now;

        // The game re-emits recent backlog into its panels on login; those
        // lines carry their original timestamp and were captured last
        // session, so drop anything meaningfully older than "now".
        var gameTimestamp = chatMessage.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(chatMessage.Timestamp).LocalDateTime
            : now;
        var replayDrop = now - gameTimestamp > TimeSpan.FromMinutes(2);
        var battleDrop = ChatTypes.IsBattleSpam(chatMessage.LogKind);

        static string Snippet(string text) => text.Length > 40 ? text[..40] : text;

        // For the first minute after load, log every event BEFORE any drop,
        // so silently filtered channels can be identified from /xllog.
        if (now - loadedAt < TimeSpan.FromSeconds(60))
        {
            Plugin.Log.Information(
                "capture: type={Type} masked={Masked} drop={Drop} sender='{Sender}' text='{Text}'",
                (ushort)chatMessage.LogKind,
                (ushort)ChatTypes.Mask(chatMessage.LogKind),
                replayDrop ? "replay" : battleDrop ? "battle" : "none",
                chatMessage.Sender.TextValue,
                Snippet(chatMessage.Message.TextValue));
        }

        if (replayDrop || battleDrop)
            return;

        // Unnamed non-battle kinds are rare; log them so mystery channels
        // can be identified even outside the diagnostic window.
        if (!Enum.IsDefined(chatMessage.LogKind))
        {
            Plugin.Log.Information(
                "unmapped chat type={Type} (masked={Masked}) sender='{Sender}' text='{Text}'",
                (ushort)chatMessage.LogKind,
                (ushort)ChatTypes.Mask(chatMessage.LogKind),
                chatMessage.Sender.TextValue,
                Snippet(chatMessage.Message.TextValue));
        }

        var senderText = chatMessage.Sender.TextValue;
        var messageText = chatMessage.Message.TextValue;
        if (chatMessage.LogKind == lastType
            && senderText == lastSender
            && messageText == lastText
            && (now - lastTimestamp).TotalMilliseconds < 300)
        {
            return;
        }

        lastType = chatMessage.LogKind;
        lastSender = senderText;
        lastText = messageText;
        lastTimestamp = now;

        var senderPlayer = MessageParser.ExtractPlayer(chatMessage.Sender);
        var isTell = chatMessage.LogKind is XivChatType.TellIncoming or XivChatType.TellOutgoing;

        // For tells, the sender field holds the other party (the recipient
        // for outgoing tells).
        var tellPartner = isTell ? senderPlayer ?? senderText : null;

        if (chatMessage.LogKind == XivChatType.TellOutgoing && tellPartner != null)
        {
            lastOutgoingTellPartner = tellPartner;
            lastOutgoingTellAt = now;
        }
        else if (!isTell && lastOutgoingTellPartner is { } attempted)
        {
            // A delivery failure arrives as an error line shortly after the
            // echo; stamp it with the attempted partner so it lands in that
            // tell tab (in addition to its normal channel routing). Matched
            // by kind and timing, not text, so it is locale-independent.
            var masked = ChatTypes.Mask(chatMessage.LogKind);
            if (masked is XivChatType.ErrorMessage or XivChatType.SystemError
                && now - lastOutgoingTellAt < TimeSpan.FromMilliseconds(500)
                && tabs.TellPartners().Contains(attempted))
            {
                tellPartner = attempted;
                lastOutgoingTellPartner = null;

                // The echo marked the partner active, but delivery failing
                // disproves that; let the other presence sources decide.
                presence.ClearActivity(attempted);
            }
        }

        var message = new Message
        {
            Timestamp = gameTimestamp,
            Type = chatMessage.LogKind,
            // The locals from the dedup check above — TextValue re-walks the
            // SeString payloads on every call.
            Sender = senderText,
            Text = messageText,
            Segments = MessageParser.Parse(chatMessage.Message),
            SenderRaw = chatMessage.Sender.Encode(),
            MessageRaw = chatMessage.Message.Encode(),
            SenderPlayer = senderPlayer,
            TellPartner = tellPartner,
            SenderJob = ResolveSenderJob(chatMessage.LogKind, senderPlayer, senderText),
        };

        // The sender's job is only knowable while they share a party with us,
        // so it is resolved at capture time and stored on the message; party
        // composition later has no bearing on what was true when it arrived.
        static uint? ResolveSenderJob(XivChatType type, string? senderPlayer, string senderText)
        {
            if (type is not (XivChatType.Party or XivChatType.CrossParty
                or XivChatType.Alliance or XivChatType.PvPTeam))
            {
                return null;
            }

            // Own messages carry no player payload; the local player is the
            // sender when the plain-text name matches. PlayerState stays
            // loaded through windows where the object-table entry is null.
            if (Plugin.PlayerState.IsLoaded
                && Plugin.PlayerState.CharacterName is { Length: > 0 } localName)
            {
                if (senderPlayer == null
                    ? senderText.EndsWith(localName, StringComparison.Ordinal)
                    : senderPlayer.StartsWith(localName + "@", StringComparison.Ordinal))
                {
                    return Plugin.PlayerState.ClassJob.RowId;
                }
            }

            if (senderPlayer == null)
                return null;

            foreach (var member in Plugin.PartyList)
            {
                var world = member.World.ValueNullable?.Name.ExtractText();
                var partner = world is { Length: > 0 }
                    ? $"{member.Name.TextValue}@{world}"
                    : member.Name.TextValue;
                if (partner == senderPlayer)
                    return member.ClassJob.RowId;
            }

            return null;
        }

        // A live line from a player proves them online; for tells the sender
        // field holds the partner. (The outgoing echo alone does NOT prove
        // delivery — the failure branch above retracts the note if an error
        // follows.) Stamped error lines must not count as partner activity.
        if (message.SenderPlayer is { } activePlayer)
            presence.NoteActivity(activePlayer);
        if (isTell && message.TellPartner is { } activePartner && activePartner != message.SenderPlayer)
            presence.NoteActivity(activePartner);

        store.Add(message);
        tabs.Route(message);
        database.Enqueue(message);

        // After routing: the line renders immediately and the translation
        // fills in underneath it whenever the provider answers.
        translation.RequestIncoming(message);
    }
}
