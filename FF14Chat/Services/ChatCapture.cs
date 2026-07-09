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

    public ChatCapture(MessageStore store, TabManager tabs)
    {
        this.store = store;
        this.tabs = tabs;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        var message = new Message
        {
            Timestamp = DateTime.Now,
            Type = chatMessage.LogKind,
            Sender = chatMessage.Sender.TextValue,
            Text = chatMessage.Message.TextValue,
            Segments = MessageParser.Parse(chatMessage.Message),
            SenderRaw = chatMessage.Sender.Encode(),
            MessageRaw = chatMessage.Message.Encode(),
            TellPartner = ExtractTellPartner(chatMessage.LogKind, chatMessage.Sender),
        };

        store.Add(message);
        tabs.Route(message);
    }

    /// <summary>
    /// For tells, the sender field holds the other party (the recipient for
    /// outgoing tells). Prefer the player payload, which carries the world.
    /// </summary>
    private static string? ExtractTellPartner(XivChatType type, SeString sender)
    {
        if (type is not (XivChatType.TellIncoming or XivChatType.TellOutgoing))
            return null;

        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload player)
            {
                var world = player.World.ValueNullable?.Name.ExtractText();
                return world is { Length: > 0 } ? $"{player.PlayerName}@{world}" : player.PlayerName;
            }
        }

        return sender.TextValue;
    }
}
