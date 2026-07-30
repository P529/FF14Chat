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
    internal IReadOnlyList<MessageSegment>? PrefixCache;
    internal MessageSegment? FallbackCache;

    /// <summary>
    /// Set from a background thread once translation resolves; read on the draw
    /// thread. Null until requested. Volatile because it is a lone reference
    /// store with no lock on either side — without it the draw thread is free
    /// to keep reading a cached null forever and the line never updates.
    /// </summary>
    internal volatile TranslationState? Translation;

    /// <summary>
    /// Identifies the most recent translation request made for this line. A
    /// batch captures it when it starts and writes its result back only if it
    /// still matches, so a slow request (retries and backoff can run for a
    /// minute) cannot land on top of a newer one for a different target
    /// language. Written with Interlocked, read with Volatile.
    /// </summary>
    internal long TranslationRequest;
}
