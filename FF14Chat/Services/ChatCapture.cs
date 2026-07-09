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
        var senderPlayer = MessageParser.ExtractPlayer(chatMessage.Sender);
        var isTell = chatMessage.LogKind is XivChatType.TellIncoming or XivChatType.TellOutgoing;

        var message = new Message
        {
            Timestamp = DateTime.Now,
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
