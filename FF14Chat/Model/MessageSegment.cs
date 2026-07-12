using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace FF14Chat.Model;

/// <summary>What a clickable run of text points at.</summary>
public abstract record SegmentLink
{
    public sealed record Item(uint ItemId, bool Hq, string? Name) : SegmentLink;

    public sealed record Map(MapLinkPayload Payload) : SegmentLink;

    /// <summary>Partner is "Name@World" (or just the name if no world resolved).</summary>
    public sealed record Player(string Partner) : SegmentLink;

    /// <summary>Target carries the scheme ("www." text gets https:// prepended).</summary>
    public sealed record Url(string Target) : SegmentLink;
}

/// <summary>
/// A run of text with uniform styling. Color null means "use the channel color".
/// </summary>
public sealed record MessageSegment(
    string Text,
    Vector4? Color,
    SegmentLink? Link);
