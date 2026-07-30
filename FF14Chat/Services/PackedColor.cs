using System.Numerics;

namespace FF14Chat.Services;

/// <summary>
/// The RGBA layout the configuration stores colors in: red in the low byte,
/// then green and blue, alpha forced opaque. Kept in one place because the
/// layout is a storage format — three copies of the shift arithmetic meant a
/// change to it (real alpha, say) had to be found by grep rather than by the
/// compiler.
/// </summary>
public static class PackedColor
{
    /// <summary>Zero is the "no override stored" sentinel, never a real color.</summary>
    public const uint None = 0;

    public static uint Pack(Vector4 color) =>
        (uint)(byte)(color.X * 255f + 0.5f)
        | ((uint)(byte)(color.Y * 255f + 0.5f) << 8)
        | ((uint)(byte)(color.Z * 255f + 0.5f) << 16)
        | 0xFF000000;

    public static Vector4 Unpack(uint rgba) => new(
        (rgba & 0xFF) / 255f,
        ((rgba >> 8) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        1f);
}
