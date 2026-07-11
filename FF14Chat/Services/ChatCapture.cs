using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FF14Chat.Model;

namespace FF14Chat.Services;

/// <summary>Subscribes to the game's chat events and feeds the message store.</summary>
public sealed class ChatCapture : IDisposable
{
    private readonly MessageStore store;
    private readonly TabManager tabs;
    private readonly MessageDatabase database;
    private readonly PresenceTracker presence;

    private XivChatType lastType;
    private string lastSender = string.Empty;
    private string lastText = string.Empty;
    private DateTime lastTimestamp;

    // Diagnostic window: log every event for the first minute after load.
    private readonly DateTime loadedAt = DateTime.Now;

    public ChatCapture(MessageStore store, TabManager tabs, MessageDatabase database, PresenceTracker presence)
    {
        this.store = store;
        this.tabs = tabs;
        this.database = database;
        this.presence = presence;
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

        // For the first minute after load, log every event BEFORE any drop,
        // so silently filtered channels can be identified from /xllog.
        if (now - loadedAt < TimeSpan.FromSeconds(60))
        {
            Plugin.Log.Information(
                "capture: type={Type} masked={Masked} drop={Drop} sender='{Sender}' text='{Text}'",
                (ushort)chatMessage.LogKind,
                (ushort)chatMessage.LogKind & 0x7F,
                replayDrop ? "replay" : battleDrop ? "battle" : "none",
                chatMessage.Sender.TextValue,
                chatMessage.Message.TextValue is { Length: > 40 } t ? t[..40] : chatMessage.Message.TextValue);
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
                (ushort)chatMessage.LogKind & 0x7F,
                chatMessage.Sender.TextValue,
                chatMessage.Message.TextValue is { Length: > 40 } t2 ? t2[..40] : chatMessage.Message.TextValue);
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

        var message = new Message
        {
            Timestamp = gameTimestamp,
            Type = chatMessage.LogKind,
            Sender = chatMessage.Sender.TextValue,
            Text = chatMessage.Message.TextValue,
            Segments = MessageParser.Parse(chatMessage.Message),
            SenderRaw = chatMessage.Sender.Encode(),
            MessageRaw = chatMessage.Message.Encode(),
            SenderPlayer = senderPlayer,
            // For tells, the sender field holds the other party (the
            // recipient for outgoing tells).
            TellPartner = isTell ? senderPlayer ?? chatMessage.Sender.TextValue : null,
        };

        // A live line from a player proves them online; for tells the sender
        // field holds the partner, and an outgoing tell only echoes back once
        // the server delivered it.
        if (message.SenderPlayer is { } activePlayer)
            presence.NoteActivity(activePlayer);
        if (message.TellPartner is { } activePartner && activePartner != message.SenderPlayer)
            presence.NoteActivity(activePartner);

        store.Add(message);
        tabs.Route(message);
        database.Enqueue(message);
    }
}
