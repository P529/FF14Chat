using System;
using System.Collections.Generic;
using Dalamud.Game.Text;

namespace FF14Chat.Model;

public sealed class Message
{
    public required DateTime Timestamp { get; init; }
    public required XivChatType Type { get; init; }
    public required string Sender { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<MessageSegment> Segments { get; init; }

    /// <summary>"Name@World" of the other party for tells, null otherwise.</summary>
    public string? TellPartner { get; init; }

    /// <summary>"Name@World" of the sending player when resolvable, null otherwise.</summary>
    public string? SenderPlayer { get; init; }

    /// <summary>ClassJob row id of the sender at the time the message arrived
    /// (party members only), null when unknown.</summary>
    public uint? SenderJob { get; init; }

    // Original SeString bytes, kept for link/color rendering (M6) and persistence (M5).
    public required byte[] SenderRaw { get; init; }
    public required byte[] MessageRaw { get; init; }

    // Render caches, draw-thread only, built lazily on first draw. All their
    // inputs are immutable except the clock format, which tags its cache.
    internal string? StampCache;
    internal bool StampCache24h;
    internal bool HasPrefixCache;
    internal MessageSegment? PrefixCache;
    internal MessageSegment? FallbackCache;
}
