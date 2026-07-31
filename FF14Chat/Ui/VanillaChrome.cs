using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FF14Chat.Ui;

/// <summary>
/// Draw helpers for the Game Default theme, which reproduces the game's own
/// chat log: a glossy grey bevel for the tab strip and the round buttons, a
/// nearly transparent input bar with one rounded cap, and the hard black drop
/// shadow every line of game text carries (the log itself has no panel behind
/// it, so the shadow is what makes text readable over the world).
///
/// Colors are sampled from a screenshot of the vanilla log.
/// </summary>
public static class VanillaChrome
{
    /// <summary>Top of the bevel gradient (#949294).</summary>
    private static readonly Vector4 BevelTop = new(0.580f, 0.572f, 0.580f, 1f);

    /// <summary>Bottom of the bevel gradient (#424142).</summary>
    private static readonly Vector4 BevelBottom = new(0.259f, 0.255f, 0.259f, 1f);

    /// <summary>Average of the gradient; fills the rounded caps.</summary>
    private static readonly Vector4 BevelMid = new(0.360f, 0.355f, 0.360f, 1f);

    private static readonly Vector4 RimColor = new(0.720f, 0.720f, 0.710f, 1f);

    /// <summary>Tab labels and button glyphs.</summary>
    public static readonly Vector4 Label = new(0.925f, 0.918f, 0.890f, 1f);

    public static readonly Vector4 LabelDim = new(0.700f, 0.696f, 0.680f, 1f);

    /// <summary>Marker dot on the selected tab (#DFD0B8).</summary>
    public static readonly Vector4 ActiveDot = new(0.875f, 0.816f, 0.722f, 1f);

    /// <summary>Channel prompt above the input bar.</summary>
    public static readonly Vector4 Prompt = new(0.937f, 0.855f, 0.639f, 1f);

    /// <summary>Unread marker; the game flashes the tab name in this tone.</summary>
    public static readonly Vector4 Unread = new(1f, 0.784f, 0.353f, 1f);

    private const uint ShadowColor = 0xD0000000;

    /// <summary>
    /// Glossy grey pill: dark rounded base, gradient over the straight middle
    /// (inset past the caps so it never spills outside the rounded silhouette)
    /// and a bright top edge.
    /// </summary>
    public static void Pill(Vector2 min, Vector2 max, float alpha, float lighten = 0f)
    {
        var drawList = ImGui.GetWindowDrawList();
        var height = max.Y - min.Y;
        var radius = height * 0.5f;

        var top = Lighten(BevelTop, lighten);
        var bottom = Lighten(BevelBottom, lighten);
        var mid = Lighten(BevelMid, lighten);

        drawList.AddRectFilled(min, max, Color(mid, alpha), radius);

        if (max.X - min.X > radius * 2f)
        {
            var topColor = Color(top, alpha);
            var bottomColor = Color(bottom, alpha);
            drawList.AddRectFilledMultiColor(
                new Vector2(min.X + radius, min.Y),
                new Vector2(max.X - radius, max.Y),
                topColor, topColor, bottomColor, bottomColor);
        }

        // Top highlight and outer rim: the two lines that read as "beveled".
        drawList.AddLine(
            new Vector2(min.X + radius, min.Y + 0.5f),
            new Vector2(max.X - radius, min.Y + 0.5f),
            Color(new Vector4(0.85f, 0.85f, 0.84f, 1f), alpha * 0.55f));
        drawList.AddRect(min, max, Color(RimColor, alpha * 0.55f), radius, ImDrawFlags.None, 1f);
    }

    /// <summary>Round bevel button (the chat-mode bubble, "+" and gear).</summary>
    public static void Circle(Vector2 center, float radius, float alpha, bool hovered)
    {
        var drawList = ImGui.GetWindowDrawList();
        var lighten = hovered ? 0.18f : 0f;

        drawList.AddCircleFilled(center, radius, Color(Lighten(BevelBottom, lighten), alpha), 24);

        // Upper cap, drawn as a smaller circle nudged up: the same top-lit look
        // as the pill without needing a gradient inside a circle.
        drawList.AddCircleFilled(
            center - new Vector2(0f, radius * 0.30f), radius * 0.78f,
            Color(Lighten(BevelTop, lighten), alpha * 0.85f), 24);

        drawList.AddCircle(center, radius, Color(RimColor, alpha * 0.60f), 24, 1f);
    }

    /// <summary>
    /// The input field's frame: square where it meets the bubble button, one
    /// rounded cap on the right, barely there fill so the world shows through.
    /// </summary>
    public static void InputBar(Vector2 min, Vector2 max, Vector4 rim, float alpha)
    {
        var drawList = ImGui.GetWindowDrawList();
        var radius = (max.Y - min.Y) * 0.5f;

        drawList.AddRectFilled(
            min, max, Color(new Vector4(0.031f, 0.035f, 0.043f, 1f), alpha * 0.55f),
            radius, ImDrawFlags.RoundCornersRight);
        drawList.AddRect(
            min, max, Color(rim, alpha * 0.70f), radius, ImDrawFlags.RoundCornersRight, 1f);
    }

    /// <summary>
    /// Soft-edged dark panel behind the log, standing in for the blurred pane
    /// the game puts there. ImGui has no blur, so the edge is feathered by
    /// stacking translucent rounded rects outward: the core reaches
    /// <paramref name="alpha"/>, the outermost ring is a single faint layer.
    /// </summary>
    public static void SoftPanel(Vector2 min, Vector2 max, Vector4 color, float alpha, float rounding, float feather)
    {
        if (alpha <= 0.004f)
            return;

        var drawList = ImGui.GetWindowDrawList();
        const int steps = 7;

        // Per-layer alpha whose `steps` stacked passes compose to `alpha`.
        var layer = 1f - MathF.Pow(1f - alpha, 1f / steps);
        var layerColor = ImGui.GetColorU32(color with { W = layer });

        for (var i = steps - 1; i >= 0; i--)
        {
            var grow = feather * (i / (float)(steps - 1));
            drawList.AddRectFilled(
                min - new Vector2(grow, grow), max + new Vector2(grow, grow),
                layerColor, rounding + grow);
        }
    }

    /// <summary>Vertical hairline between two tab cells.</summary>
    public static void Divider(Vector2 top, float height, float alpha)
    {
        var drawList = ImGui.GetWindowDrawList();
        var inset = height * 0.18f;
        drawList.AddLine(
            top + new Vector2(0f, inset), top + new Vector2(0f, height - inset),
            Color(RimColor, alpha * 0.45f), 1f);
    }

    /// <summary>
    /// Queues the drop shadow for a text item about to be submitted at the
    /// current cursor. The real text is drawn by the caller on top of it —
    /// this only adds to the draw list, so it never disturbs layout.
    /// </summary>
    public static void ShadowAtCursor(ReadOnlySpan<char> text)
    {
        ImGui.GetWindowDrawList().AddText(ImGui.GetCursorScreenPos() + Vector2.One, ShadowColor, text);
    }

    /// <summary>Shadowed text at an explicit screen position (custom-drawn chrome).</summary>
    public static void Text(Vector2 pos, Vector4 color, ReadOnlySpan<char> text)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(pos + Vector2.One, ShadowColor, text);
        drawList.AddText(pos, ImGui.GetColorU32(color), text);
    }

    /// <summary>Speech balloon on the chat-mode button.</summary>
    public static void BubbleGlyph(Vector2 center, float size, uint color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var half = size * 0.5f;
        var body = new Vector2(half, half * 0.72f);

        drawList.AddRectFilled(center - body, center + body, color, half * 0.45f);
        drawList.AddTriangleFilled(
            center + new Vector2(-half * 0.55f, body.Y - 0.5f),
            center + new Vector2(-half * 0.05f, body.Y - 0.5f),
            center + new Vector2(-half * 0.75f, body.Y + half * 0.55f),
            color);
    }

    public static void PlusGlyph(Vector2 center, float size, uint color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var arm = size * 0.5f;
        drawList.AddLine(center - new Vector2(arm, 0f), center + new Vector2(arm, 0f), color, 2f);
        drawList.AddLine(center - new Vector2(0f, arm), center + new Vector2(0f, arm), color, 2f);
    }

    public static void GearGlyph(Vector2 center, float size, uint color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var radius = size * 0.30f;

        drawList.AddCircle(center, radius, color, 12, 1.5f);
        for (var i = 0; i < 8; i++)
        {
            var angle = MathF.Tau * i / 8f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            drawList.AddLine(center + direction * radius, center + direction * (radius + size * 0.16f), color, 1.5f);
        }
    }

    public static void LockGlyph(Vector2 center, float size, uint color, bool locked)
    {
        var drawList = ImGui.GetWindowDrawList();
        var scale = size / 16f;
        var bodyTop = center.Y + size * 0.02f;

        drawList.AddRectFilled(
            new Vector2(center.X - 4f * scale, bodyTop),
            new Vector2(center.X + 4f * scale, bodyTop + 6f * scale),
            color, 1.5f * scale);

        if (locked)
            drawList.PathArcTo(new Vector2(center.X, bodyTop), 2.8f * scale, MathF.PI, MathF.Tau);
        else
            drawList.PathArcTo(new Vector2(center.X + 1.4f * scale, bodyTop - 0.5f * scale), 2.8f * scale, MathF.PI * 0.95f, MathF.PI * 1.75f);

        drawList.PathStroke(color, ImDrawFlags.None, 1.5f * scale);
    }

    public static void CrossGlyph(Vector2 center, float size, uint color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var arm = size * 0.5f;
        drawList.AddLine(center - new Vector2(arm, arm), center + new Vector2(arm, arm), color, 1.5f);
        drawList.AddLine(center + new Vector2(arm, -arm), center + new Vector2(-arm, arm), color, 1.5f);
    }

    private static Vector4 Lighten(Vector4 color, float amount) => amount <= 0f
        ? color
        : new Vector4(
            color.X + (1f - color.X) * amount,
            color.Y + (1f - color.Y) * amount,
            color.Z + (1f - color.Z) * amount,
            color.W);

    private static uint Color(Vector4 color, float alpha) => ImGui.GetColorU32(color with { W = color.W * alpha });
}
