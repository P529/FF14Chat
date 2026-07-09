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

    private XivChatType lastType;
    private string lastSender = string.Empty;
    private string lastText = string.Empty;
    private DateTime lastTimestamp;

    public ChatCapture(MessageStore store, TabManager tabs, MessageDatabase database)
    {
        this.store = store;
        this.tabs = tabs;
        this.database = database;
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
        if (now - gameTimestamp > TimeSpan.FromMinutes(2))
            return;

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

        store.Add(message);
        tabs.Route(message);
        database.Enqueue(message);
    }
}
