using System;
using Dalamud.Game.Chat;
using FF14Chat.Model;

namespace FF14Chat.Services;

/// <summary>Subscribes to the game's chat events and feeds the message store.</summary>
public sealed class ChatCapture : IDisposable
{
    private readonly MessageStore store;

    public ChatCapture(MessageStore store)
    {
        this.store = store;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        store.Add(new Message
        {
            Timestamp = DateTime.Now,
            Type = message.LogKind,
            Sender = message.Sender.TextValue,
            Text = message.Message.TextValue,
            SenderRaw = message.Sender.Encode(),
            MessageRaw = message.Message.Encode(),
        });
    }
}
