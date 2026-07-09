using System.Numerics;

namespace FF14Chat.Model;

/// <summary>
/// A run of text with uniform styling. Color null means "use the channel color".
/// ItemId non-null means this run is part of an item link.
/// </summary>
public sealed record MessageSegment(
    string Text,
    Vector4? Color,
    uint? ItemId,
    bool ItemHq,
    string? ItemName);
