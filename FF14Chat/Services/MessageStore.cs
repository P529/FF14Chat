using System.Collections.Generic;
using FF14Chat.Model;

namespace FF14Chat.Services;

/// <summary>
/// In-memory ring buffer of chat messages. Writers (game thread) and the
/// renderer (draw thread) synchronize on the internal lock.
/// </summary>
public sealed class MessageStore
{
    private const int MaxMessages = 10_000;

    private readonly List<Message> messages = [];
    private readonly object gate = new();

    public void Add(Message message)
    {
        lock (gate)
        {
            messages.Add(message);
            if (messages.Count > MaxMessages)
                messages.RemoveRange(0, messages.Count - MaxMessages);
        }
    }

    public Message[] Snapshot()
    {
        lock (gate)
        {
            return [.. messages];
        }
    }
}
