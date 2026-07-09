using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace FF14Chat.Ui;

/// <summary>
/// Square Enix-style look: warm parchment text and muted gold accents on a
/// dark, slightly blue charcoal panel, matching the game's own windows.
/// </summary>
public static class FFTheme
{
    // Palette sampled from the vanilla UI.
    public static readonly Vector4 Gold = new(0.784f, 0.667f, 0.431f, 1f);        // #C8AA6E
    public static readonly Vector4 GoldBright = new(0.910f, 0.835f, 0.628f, 1f);  // #E8D5A0
    public static readonly Vector4 TextWarm = new(0.910f, 0.886f, 0.816f, 1f);    // #E8E2D0
    public static readonly Vector4 TextDim = new(0.651f, 0.631f, 0.565f, 1f);     // #A6A190

    public static readonly Vector4 BgBottom = new(0.078f, 0.078f, 0.098f, 0.945f); // #14141A
    public static readonly Vector4 BgSheen = new(0.216f, 0.216f, 0.271f, 0.55f);   // #373745 fading down

    private static readonly Vector4 FrameBg = new(0.055f, 0.055f, 0.075f, 0.90f);
    private static readonly Vector4 FrameBgHover = new(0.100f, 0.100f, 0.130f, 0.95f);
    private static readonly Vector4 FrameBgActive = new(0.120f, 0.120f, 0.155f, 1f);
    private static readonly Vector4 TabIdle = new(0.130f, 0.130f, 0.165f, 0.85f);
    private static readonly Vector4 TabHover = new(0.230f, 0.225f, 0.270f, 1f);
    private static readonly Vector4 TabSelected = new(0.265f, 0.255f, 0.300f, 1f);
    private static readonly Vector4 PanelBg = new(0.100f, 0.100f, 0.128f, 0.96f);

    public static ImRaii.ColorDisposable PushColors() => ImRaii.PushColor(ImGuiCol.Text, TextWarm)
        .Push(ImGuiCol.TextDisabled, TextDim)
        .Push(ImGuiCol.WindowBg, PanelBg)
        .Push(ImGuiCol.PopupBg, PanelBg)
        .Push(ImGuiCol.Border, Gold with { W = 0.40f })
        .Push(ImGuiCol.Separator, Gold with { W = 0.30f })
        .Push(ImGuiCol.FrameBg, FrameBg)
        .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
        .Push(ImGuiCol.FrameBgActive, FrameBgActive)
        .Push(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0.10f))
        .Push(ImGuiCol.ScrollbarGrab, Gold with { W = 0.30f })
        .Push(ImGuiCol.ScrollbarGrabHovered, Gold with { W = 0.55f })
        .Push(ImGuiCol.ScrollbarGrabActive, Gold with { W = 0.75f })
        .Push(ImGuiCol.Tab, TabIdle)
        .Push(ImGuiCol.TabHovered, TabHover)
        .Push(ImGuiCol.TabActive, TabSelected)
        .Push(ImGuiCol.TabUnfocused, TabIdle)
        .Push(ImGuiCol.TabUnfocusedActive, TabSelected)
        .Push(ImGuiCol.Button, new Vector4(0.165f, 0.165f, 0.205f, 0.9f))
        .Push(ImGuiCol.ButtonHovered, TabHover)
        .Push(ImGuiCol.ButtonActive, TabSelected)
        .Push(ImGuiCol.Header, Gold with { W = 0.16f })
        .Push(ImGuiCol.HeaderHovered, Gold with { W = 0.26f })
        .Push(ImGuiCol.HeaderActive, Gold with { W = 0.34f })
        .Push(ImGuiCol.TextSelectedBg, Gold with { W = 0.30f })
        .Push(ImGuiCol.CheckMark, GoldBright)
        .Push(ImGuiCol.SliderGrab, Gold)
        .Push(ImGuiCol.SliderGrabActive, GoldBright)
        .Push(ImGuiCol.ResizeGrip, Gold with { W = 0.20f })
        .Push(ImGuiCol.ResizeGripHovered, Gold with { W = 0.45f })
        .Push(ImGuiCol.ResizeGripActive, Gold with { W = 0.65f });

    public static ImRaii.StyleDisposable PushStyles() => ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 9f)
        .Push(ImGuiStyleVar.ChildRounding, 4f)
        .Push(ImGuiStyleVar.FrameRounding, 4f)
        .Push(ImGuiStyleVar.PopupRounding, 6f)
        .Push(ImGuiStyleVar.TabRounding, 4f)
        .Push(ImGuiStyleVar.ScrollbarRounding, 12f)
        .Push(ImGuiStyleVar.ScrollbarSize, 10f)
        .Push(ImGuiStyleVar.WindowBorderSize, 0f)
        .Push(ImGuiStyleVar.ChildBorderSize, 0f)
        .Push(ImGuiStyleVar.PopupBorderSize, 1f)
        .Push(ImGuiStyleVar.FrameBorderSize, 1f)
        .Push(ImGuiStyleVar.FramePadding, new Vector2(9f, 5f))
        .Push(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));

    /// <summary>Rounded panel fill with a soft top sheen and a double border, FFXIV-style.</summary>
    public static void DrawWindowBackground()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        const float rounding = 9f;

        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(BgBottom), rounding);

        // Vertical sheen over the top part, inset past the corners.
        var sheenTop = ImGui.GetColorU32(BgSheen);
        drawList.AddRectFilledMultiColor(
            pos + new Vector2(rounding, 1f),
            new Vector2(pos.X + size.X - rounding, pos.Y + size.Y * 0.38f),
            sheenTop, sheenTop, 0, 0);

        // Outer gold hairline plus an inner dark line for the embossed look.
        drawList.AddRect(pos, pos + size, ImGui.GetColorU32(Gold with { W = 0.55f }), rounding, ImDrawFlags.None, 1f);
        drawList.AddRect(pos + Vector2.One, pos + size - Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f)), rounding - 1f, ImDrawFlags.None, 1f);
    }

    /// <summary>Thin gold rule that fades out toward both ends.</summary>
    public static void DrawFadingSeparator(Vector2 screenPos, float width)
    {
        var drawList = ImGui.GetWindowDrawList();
        var gold = ImGui.GetColorU32(Gold with { W = 0.75f });
        var mid = screenPos + new Vector2(width * 0.5f, 0);
        var end = screenPos + new Vector2(width, 0);

        drawList.AddRectFilledMultiColor(screenPos, mid + new Vector2(0, 1f), 0, gold, gold, 0);
        drawList.AddRectFilledMultiColor(mid, end + new Vector2(0, 1f), gold, 0, 0, gold);
    }
}
