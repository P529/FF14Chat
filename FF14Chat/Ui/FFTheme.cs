using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace FF14Chat.Ui;

/// <summary>
/// Window theming. Muted/Rich Gold sample the vanilla FFXIV UI; Classic Blue
/// is the white-on-blue menu of the 2D-era games; FF7 Remake is the dark
/// ink-blue panel with bright cyan accents and squared corners.
/// </summary>
public static class FFTheme
{
    private static ChatTheme theme = ChatTheme.MutedGold;
    private static float opacity = 0.78f;

    /// <summary>Call once per frame before pushing colors, from PreDraw.</summary>
    public static void Configure(Configuration config)
    {
        theme = (ChatTheme)config.Theme;
        opacity = System.Math.Clamp(config.BgOpacity, 0.3f, 1f);
    }

    /// <summary>Primary accent (borders, separators, scrollbars).</summary>
    public static Vector4 Gold => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.860f, 0.880f, 0.960f, 1f),
        ChatTheme.Ff7Remake => new Vector4(0.310f, 0.780f, 0.940f, 1f),
        ChatTheme.RichGold => new Vector4(0.784f, 0.667f, 0.431f, 1f),   // #C8AA6E
        _ => new Vector4(0.640f, 0.600f, 0.505f, 1f),                    // #A3997F
    };

    /// <summary>Brighter accent (title, hover states).</summary>
    public static Vector4 GoldBright => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(1f, 1f, 1f, 1f),
        ChatTheme.Ff7Remake => new Vector4(0.650f, 0.900f, 1f, 1f),
        ChatTheme.RichGold => new Vector4(0.910f, 0.835f, 0.628f, 1f),   // #E8D5A0
        _ => new Vector4(0.800f, 0.770f, 0.680f, 1f),                    // #CCC4AD
    };

    public static Vector4 TextWarm => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.965f, 0.965f, 0.985f, 1f),
        ChatTheme.Ff7Remake => new Vector4(0.940f, 0.960f, 0.980f, 1f),
        _ => new Vector4(0.900f, 0.885f, 0.835f, 1f),
    };

    public static Vector4 TextDim => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.700f, 0.730f, 0.870f, 1f),
        ChatTheme.Ff7Remake => new Vector4(0.550f, 0.630f, 0.720f, 1f),
        _ => new Vector4(0.630f, 0.615f, 0.560f, 1f),
    };

    public static Vector4 BgBottom => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.010f, 0.020f, 0.310f, opacity),
        ChatTheme.Ff7Remake => new Vector4(0.028f, 0.062f, 0.105f, opacity),
        _ => new Vector4(0.078f, 0.078f, 0.098f, opacity),
    };

    public static Vector4 BgSheen => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.180f, 0.260f, 0.760f, 0.50f * opacity),
        ChatTheme.Ff7Remake => new Vector4(0.100f, 0.220f, 0.360f, 0.45f * opacity),
        ChatTheme.RichGold => new Vector4(0.216f, 0.216f, 0.271f, 0.55f * opacity),
        _ => new Vector4(0.216f, 0.216f, 0.271f, 0.30f * opacity),
    };

    /// <summary>Corner rounding; FF7 Remake panels are squared.</summary>
    public static float Rounding => theme == ChatTheme.Ff7Remake ? 2f : 9f;

    private static float SmallRounding => theme == ChatTheme.Ff7Remake ? 1f : 4f;

    private static float BorderAlpha => theme switch
    {
        ChatTheme.ClassicBlue => 0.90f,
        ChatTheme.Ff7Remake => 0.60f,
        ChatTheme.RichGold => 0.55f,
        _ => 0.40f,
    };

    private static float BorderThickness => theme == ChatTheme.ClassicBlue ? 2f : 1f;

    private static Vector4 FrameBg => WithOpacity(theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.005f, 0.010f, 0.220f, 0.85f),
        ChatTheme.Ff7Remake => new Vector4(0.045f, 0.095f, 0.150f, 0.85f),
        ChatTheme.RichGold => new Vector4(0.055f, 0.055f, 0.075f, 0.90f),
        _ => new Vector4(0.055f, 0.055f, 0.075f, 0.70f),
    });

    private static Vector4 FrameBgHover => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.040f, 0.070f, 0.330f, 0.92f),
        ChatTheme.Ff7Remake => new Vector4(0.075f, 0.150f, 0.230f, 0.92f),
        ChatTheme.RichGold => new Vector4(0.100f, 0.100f, 0.130f, 0.95f),
        _ => new Vector4(0.100f, 0.100f, 0.130f, 0.80f),
    };

    private static Vector4 FrameBgActive => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.070f, 0.110f, 0.400f, 1f),
        ChatTheme.Ff7Remake => new Vector4(0.095f, 0.190f, 0.290f, 1f),
        ChatTheme.RichGold => new Vector4(0.120f, 0.120f, 0.155f, 1f),
        _ => new Vector4(0.120f, 0.120f, 0.155f, 0.90f),
    };

    /// <summary>Scales a fill's alpha with the window opacity.</summary>
    private static Vector4 WithOpacity(Vector4 color) => color with { W = color.W * opacity };

    // Tab fills scale with the window opacity so the selected tab doesn't
    // sit as an opaque block on a translucent panel.
    private static Vector4 TabIdle => WithOpacity(theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.030f, 0.060f, 0.340f, 0.70f),
        ChatTheme.Ff7Remake => new Vector4(0.060f, 0.130f, 0.200f, 0.65f),
        ChatTheme.RichGold => new Vector4(0.130f, 0.130f, 0.165f, 0.85f),
        _ => new Vector4(0.130f, 0.130f, 0.165f, 0.55f),
    });

    private static Vector4 TabHover => WithOpacity(theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.110f, 0.170f, 0.520f, 0.95f),
        ChatTheme.Ff7Remake => new Vector4(0.120f, 0.290f, 0.430f, 0.95f),
        ChatTheme.RichGold => new Vector4(0.230f, 0.225f, 0.270f, 1f),
        _ => new Vector4(0.230f, 0.225f, 0.270f, 0.85f),
    });

    private static Vector4 TabSelected => WithOpacity(theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.150f, 0.220f, 0.580f, 0.90f),
        ChatTheme.Ff7Remake => new Vector4(0.145f, 0.350f, 0.520f, 0.90f),
        ChatTheme.RichGold => new Vector4(0.265f, 0.255f, 0.300f, 0.90f),
        _ => new Vector4(0.265f, 0.255f, 0.300f, 0.85f),
    });

    private static Vector4 PanelBg => theme switch
    {
        ChatTheme.ClassicBlue => new Vector4(0.020f, 0.030f, 0.300f, 0.96f),
        ChatTheme.Ff7Remake => new Vector4(0.035f, 0.075f, 0.125f, 0.96f),
        _ => new Vector4(0.100f, 0.100f, 0.128f, 0.96f),
    };

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
        .Push(ImGuiCol.Button, FrameBgHover)
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

    public static ImRaii.StyleDisposable PushStyles() => ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Rounding)
        .Push(ImGuiStyleVar.ChildRounding, SmallRounding)
        .Push(ImGuiStyleVar.FrameRounding, SmallRounding)
        .Push(ImGuiStyleVar.PopupRounding, SmallRounding)
        .Push(ImGuiStyleVar.TabRounding, SmallRounding)
        .Push(ImGuiStyleVar.ScrollbarRounding, 12f)
        .Push(ImGuiStyleVar.ScrollbarSize, 10f)
        .Push(ImGuiStyleVar.WindowBorderSize, 0f)
        .Push(ImGuiStyleVar.ChildBorderSize, 0f)
        .Push(ImGuiStyleVar.PopupBorderSize, 1f)
        .Push(ImGuiStyleVar.FrameBorderSize, 1f)
        .Push(ImGuiStyleVar.FramePadding, new Vector2(9f, 5f))
        .Push(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));

    /// <summary>Rounded panel fill with a soft top sheen and a double border.</summary>
    public static void DrawWindowBackground()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var rounding = Rounding;

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
        drawList.AddRect(pos + Vector2.One, pos + size - Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f)), System.Math.Max(rounding - 1f, 0f), ImDrawFlags.None, 1f);
    }

    /// <summary>Thin accent rule that fades out toward both ends.</summary>
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
