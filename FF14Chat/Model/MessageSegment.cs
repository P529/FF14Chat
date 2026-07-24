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

    /// <summary>Id of a completed achievement; opens the Achievement window.</summary>
    public sealed record Achievement(uint Id) : SegmentLink;

    /// <summary>Party Finder link. Notification opens the finder window; a
    /// listing (Notification false) opens that specific recruitment.</summary>
    public sealed record PartyFinder(uint ListingId, bool Notification) : SegmentLink;

    /// <summary>Quest name; opens the quest in the journal.</summary>
    public sealed record Quest(uint QuestRowId) : SegmentLink;

    /// <summary>Status effect; hover shows its name (no click action).</summary>
    public sealed record Status(uint StatusId) : SegmentLink;

    /// <summary>A chat link another plugin inserted; click re-fires its handler.</summary>
    public sealed record Dalamud(DalamudLinkPayload Payload) : SegmentLink;
}

/// <summary>
/// A run of text with uniform styling. Color null means "use the channel color".
/// Emote carries the emoji to draw as a Twemoji image; Text then holds the
/// ":shortcode:" the emoji replaces, kept as the plaintext/render fallback.
/// IconId (non-zero) is a game BitmapFontIcon drawn from the font-icon sheet;
/// Text is then the empty replacement kept as the fallback.
/// </summary>
public sealed record MessageSegment(
    string Text,
    Vector4? Color,
    SegmentLink? Link,
    string? Emote = null,
    uint IconId = 0)
{
    /// <summary>
    /// Render cache of word tokens ("\n" entries force a line break), built
    /// lazily on the draw thread — Text is immutable, so once is enough.
    /// </summary>
    internal string[]? Tokens;
}
