using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FF14Chat.Ui;

public class SettingsWindow : Window, IDisposable
{
    private static readonly int[] FontSizes = [10, 12, 14, 18];

    private readonly Plugin plugin;
    private readonly MainWindow mainWindow;

    private ImRaii.ColorDisposable? themeColors;
    private ImRaii.StyleDisposable? themeStyles;

    public SettingsWindow(Plugin plugin, MainWindow mainWindow) : base("FF14Chat Settings###FF14ChatSettings")
    {
        this.plugin = plugin;
        this.mainWindow = mainWindow;

        Size = new Vector2(320, 200);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        themeColors = FFTheme.PushColors();
        themeStyles = FFTheme.PushStyles();
    }

    public override void PostDraw()
    {
        themeStyles?.Dispose();
        themeStyles = null;
        themeColors?.Dispose();
        themeColors = null;
    }

    public override void Draw()
    {
        var config = plugin.Configuration;

        var sizeIndex = Array.IndexOf(FontSizes, config.FontSize);
        if (sizeIndex < 0)
            sizeIndex = 1;

        ImGui.SetNextItemWidth(120f);
        if (ImGui.Combo("Font size", ref sizeIndex, ["10", "12", "14", "18"], FontSizes.Length))
        {
            config.FontSize = FontSizes[sizeIndex];
            config.Save();
            mainWindow.RebuildFont();
        }

        var hideVanilla = config.HideVanillaChat;
        if (ImGui.Checkbox("Hide vanilla chat while open", ref hideVanilla))
        {
            config.HideVanillaChat = hideVanilla;
            config.Save();
        }

        var locked = config.LockWindow;
        if (ImGui.Checkbox("Lock window position and size", ref locked))
        {
            config.LockWindow = locked;
            config.Save();
        }

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.TextUnformatted("History: last 30 days, restored on login.");
        }
    }
}
