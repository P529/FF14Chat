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
    private static ChatTheme theme = ChatTheme.MutedGold;
    private static float opacity = 0.78f;

    private static bool Classic => theme == ChatTheme.ClassicBlue;
    private static bool MutedAccents => theme != ChatTheme.RichGold;

    /// <summary>Call once per frame before pushing colors, from PreDraw.</summary>
    public static void Configure(Configuration config)
    {
        theme = (ChatTheme)config.Theme;
        opacity = System.Math.Clamp(config.BgOpacity, 0.3f, 1f);
    }

    // Gold/Muted: palette sampled from the vanilla UI. Classic Blue: the
    // white-on-blue menu look of the 2D-era Final Fantasy games.
    public static Vector4 Gold => Classic
        ? new Vector4(0.860f, 0.880f, 0.960f, 1f)
        : MutedAccents
            ? new Vector4(0.640f, 0.600f, 0.505f, 1f)   // #A3997F
            : new Vector4(0.784f, 0.667f, 0.431f, 1f);  // #C8AA6E

    public static Vector4 GoldBright => Classic
        ? new Vector4(1f, 1f, 1f, 1f)
        : MutedAccents
            ? new Vector4(0.800f, 0.770f, 0.680f, 1f)   // #CCC4AD
            : new Vector4(0.910f, 0.835f, 0.628f, 1f);  // #E8D5A0

    public static Vector4 TextWarm => Classic
        ? new Vector4(0.965f, 0.965f, 0.985f, 1f)
        : new Vector4(0.900f, 0.885f, 0.835f, 1f);

    public static Vector4 TextDim => Classic
        ? new Vector4(0.700f, 0.730f, 0.870f, 1f)
        : new Vector4(0.630f, 0.615f, 0.560f, 1f);

    public static Vector4 BgBottom => Classic
        ? new Vector4(0.010f, 0.020f, 0.310f, opacity)
        : new Vector4(0.078f, 0.078f, 0.098f, opacity);

    public static Vector4 BgSheen => Classic
        ? new Vector4(0.180f, 0.260f, 0.760f, 0.50f * opacity)
        : new Vector4(0.216f, 0.216f, 0.271f, (MutedAccents ? 0.30f : 0.55f) * opacity);

    private static float BorderAlpha => Classic ? 0.90f : MutedAccents ? 0.40f : 0.55f;
    private static float BorderThickness => Classic ? 2f : 1f;

    private static Vector4 FrameBg => Classic
        ? new Vector4(0.005f, 0.010f, 0.220f, 0.85f)
        : new Vector4(0.055f, 0.055f, 0.075f, MutedAccents ? 0.70f : 0.90f);

    private static Vector4 FrameBgHover => Classic
        ? new Vector4(0.040f, 0.070f, 0.330f, 0.92f)
        : new Vector4(0.100f, 0.100f, 0.130f, MutedAccents ? 0.80f : 0.95f);

    private static Vector4 FrameBgActive => Classic
        ? new Vector4(0.070f, 0.110f, 0.400f, 1f)
        : new Vector4(0.120f, 0.120f, 0.155f, MutedAccents ? 0.90f : 1f);

    private static Vector4 TabIdle => Classic
        ? new Vector4(0.030f, 0.060f, 0.340f, 0.70f)
        : new Vector4(0.130f, 0.130f, 0.165f, MutedAccents ? 0.55f : 0.85f);

    private static Vector4 TabHover => Classic
        ? new Vector4(0.110f, 0.170f, 0.520f, 0.95f)
        : new Vector4(0.230f, 0.225f, 0.270f, MutedAccents ? 0.85f : 1f);

    private static Vector4 TabSelected => Classic
        ? new Vector4(0.150f, 0.220f, 0.580f, 1f)
        : new Vector4(0.265f, 0.255f, 0.300f, MutedAccents ? 0.90f : 1f);

    private static Vector4 PanelBg => Classic
        ? new Vector4(0.020f, 0.030f, 0.300f, 0.96f)
        : new Vector4(0.100f, 0.100f, 0.128f, 0.96f);

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

        // Outer accent line plus an inner dark line for the embossed look
        // (Classic Blue gets the thick white double border of the old menus).
        drawList.AddRect(pos, pos + size, ImGui.GetColorU32(Gold with { W = BorderAlpha }), rounding, ImDrawFlags.None, BorderThickness);
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
