using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FF14Chat.Ui;

public partial class SettingsWindow : Window, IDisposable
{
    private static readonly int[] FontSizes = [10, 12, 14, 18];

    private static readonly (string Group, (XivChatType Type, string Label)[] Channels)[] ChannelGroups =
    [
        ("Chat",
        [
            (XivChatType.Say, "Say"), (XivChatType.Shout, "Shout"), (XivChatType.Yell, "Yell"),
            (XivChatType.TellIncoming, "Tell (in)"), (XivChatType.TellOutgoing, "Tell (out)"),
            (XivChatType.Party, "Party"), (XivChatType.CrossParty, "CW Party"),
            (XivChatType.Alliance, "Alliance"), (XivChatType.PvPTeam, "PvP Team"),
            (XivChatType.FreeCompany, "Free Company"), (XivChatType.NoviceNetwork, "Novice Network"),
            (XivChatType.StandardEmote, "Emote"), (XivChatType.CustomEmote, "Emote (custom)"),
            (XivChatType.Echo, "Echo"),
        ]),
        ("Linkshells",
        [
            (XivChatType.Ls1, "LS1"), (XivChatType.Ls2, "LS2"), (XivChatType.Ls3, "LS3"),
            (XivChatType.Ls4, "LS4"), (XivChatType.Ls5, "LS5"), (XivChatType.Ls6, "LS6"),
            (XivChatType.Ls7, "LS7"), (XivChatType.Ls8, "LS8"),
            (XivChatType.CrossLinkShell1, "CWLS1"), (XivChatType.CrossLinkShell2, "CWLS2"),
            (XivChatType.CrossLinkShell3, "CWLS3"), (XivChatType.CrossLinkShell4, "CWLS4"),
            (XivChatType.CrossLinkShell5, "CWLS5"), (XivChatType.CrossLinkShell6, "CWLS6"),
            (XivChatType.CrossLinkShell7, "CWLS7"), (XivChatType.CrossLinkShell8, "CWLS8"),
        ]),
        ("System",
        [
            (XivChatType.SystemMessage, "System"), (XivChatType.SystemError, "System Error"),
            (XivChatType.ErrorMessage, "Error"), (XivChatType.Notice, "Notice"),
            (XivChatType.Urgent, "Urgent"), (XivChatType.GatheringSystemMessage, "Gathering"),
            (XivChatType.RetainerSale, "Retainer Sale"), (XivChatType.NPCDialogue, "NPC Dialogue"),
            (XivChatType.NPCDialogueAnnouncements, "NPC Announce"),
        ]),
    ];

    /// <summary>
    /// Assembly version + on-disk build time, so a loaded build can be
    /// verified at a glance (shown in the settings title and header).
    /// </summary>
    internal static readonly string BuildStamp = MakeBuildStamp();

    private static string MakeBuildStamp()
    {
        try
        {
            var asm = typeof(SettingsWindow).Assembly;
            var ver = asm.GetName().Version;
            var built = System.IO.File.GetLastWriteTime(asm.Location);
            return $"v{ver} · built {built:yyyy-MM-dd HH:mm:ss}";
        }
        catch
        {
            return "build unknown";
        }
    }

    private readonly Plugin plugin;
    private readonly MainWindow mainWindow;

    /// <summary>Tab name at the moment its rename field gained focus.</summary>
    private string renameOriginal = string.Empty;

    private ImRaii.ColorDisposable? themeColors;
    private ImRaii.StyleDisposable? themeStyles;

    public SettingsWindow(Plugin plugin, MainWindow mainWindow)
        : base($"FF14Chat Settings — {BuildStamp}###FF14ChatSettings")
    {
        this.plugin = plugin;
        this.mainWindow = mainWindow;

        Size = new Vector2(430, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        FFTheme.Configure(plugin.Configuration);
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

        ImGui.TextDisabled(BuildStamp);

        using var tabBar = ImRaii.TabBar("##settings");
        if (!tabBar.Success)
            return;

        using (var tab = ImRaii.TabItem("General"))
        {
            if (tab.Success)
                DrawGeneralTab(config);
        }

        using (var tab = ImRaii.TabItem("Colors"))
        {
            if (tab.Success)
                DrawColorsTab(config);
        }

        using (var tab = ImRaii.TabItem("History"))
        {
            if (tab.Success)
                DrawHistoryTab(config);
        }

        using (var tab = ImRaii.TabItem("Translate"))
        {
            if (tab.Success)
                DrawTranslateTab(config);
        }
    }

    /// <summary>Checkbox bound to a config flag; saves on change.</summary>
    private static void Toggle(
        Configuration config, string label, bool value,
        Action<Configuration, bool> apply, string? tooltip = null)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            apply(config, value);
            config.Save();
        }

        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawGeneralTab(Configuration config)
    {
        SectionHeader("Appearance", first: true);

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

        var themeIndex = config.Theme;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo("Theme", ref themeIndex, ["Muted Gold", "Rich Gold", "Classic Blue", "FF7 Remake"], 4))
        {
            config.Theme = themeIndex;
            config.Save();
        }

        var bgOpacity = config.BgOpacity;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Background opacity", ref bgOpacity, 0.3f, 1f, "%.2f"))
        {
            config.BgOpacity = bgOpacity;
            config.Save();
        }

        SectionHeader("Window");

        Toggle(config, "Hide vanilla chat while open",
            config.HideVanillaChat, static (c, v) => c.HideVanillaChat = v);
        Toggle(config, "Lock window position and size",
            config.LockWindow, static (c, v) => c.LockWindow = v);
        Toggle(config, "Hide during cutscenes",
            config.HideDuringCutscenes, static (c, v) => c.HideDuringCutscenes = v);
        Toggle(config, "Hide when game UI is hidden",
            config.HideWhenUiHidden, static (c, v) => c.HideWhenUiHidden = v);
        Toggle(config, "Hide on loading screens",
            config.HideInLoadingScreens, static (c, v) => c.HideInLoadingScreens = v);
        Toggle(config, "Hide in combat",
            config.HideInBattle, static (c, v) => c.HideInBattle = v);

        SectionHeader("Chat display");

        Toggle(config, "Highlight messages mentioning you",
            config.HighlightMentions, static (c, v) => c.HighlightMentions = v);
        Toggle(config, "Role-colored names in party chat",
            config.RoleColorPartyNames, static (c, v) => c.RoleColorPartyNames = v,
            "Tank blue, healer green, DPS red.\nOnly for messages received while in the party.");
        Toggle(config, "Job icon before names in party chat",
            config.JobIconPartyNames, static (c, v) => c.JobIconPartyNames = v);
        Toggle(config, "Online status dot on tell tabs",
            config.ShowTellPresence, static (c, v) => c.ShowTellPresence = v,
            "Green: online, red: AFK, gray: offline, blue: unknown.\nTracked via friend list, party, and nearby players;\nnon-friends who are elsewhere can't be looked up (blue).");
        Toggle(config, "24-hour timestamps",
            config.Use24HourClock, static (c, v) => c.Use24HourClock = v);
        Toggle(config, "Collapse repeated messages",
            config.CollapseDuplicates, static (c, v) => c.CollapseDuplicates = v,
            "Consecutive identical lines show once with a ×N counter.");
        Toggle(config, "Emotes (:sob: shows the emoji)",
            config.RenderEmotes, static (c, v) => c.RenderEmotes = v,
            "Discord-style shortcodes render as emoji images; type : plus two letters for suggestions.\nArtwork: Twemoji (CC-BY 4.0), bundled with the plugin — nothing is downloaded.");
        Toggle(config, "Tab cycles completions, Space accepts",
            config.TabCyclesSuggestions, static (c, v) => c.TabCyclesSuggestions = v,
            "While command/emote completions are open:\nTab moves to the next option (Shift+Tab previous), Space locks it in.\nOff: Tab accepts the highlighted option immediately.");
        Toggle(config, "Native item tooltips",
            config.NativeItemTooltips, static (c, v) => c.NativeItemTooltips = v,
            "The game's own tooltip on item links instead of the plugin's card.");

        SectionHeader("Tabs");

        // The combined tab is keyed off tabs literally named General/System;
        // without them the flag does nothing, so don't offer it.
        var hasCombinePair = config.Tabs.Exists(t => t.Name is "General" or "System");
        var combine = config.CombineGeneralSystem;
        using (ImRaii.Disabled(!hasCombinePair))
        {
            if (ImGui.Checkbox("Combine General and System into \"All\"", ref combine))
            {
                config.CombineGeneralSystem = combine;
                ApplyTabChanges(config);
            }
        }

        if (!hasCombinePair && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Requires a tab named \"General\" or \"System\".");

        if (ImGui.CollapsingHeader("Tab editor"))
            DrawTabEditor(config);
    }

    private void DrawColorsTab(Configuration config)
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.TextWrapped("Message color per channel. Modified channels show a reset button.");
        }

        foreach (var (group, channels) in ChannelGroups)
        {
            SectionHeader(group);

            foreach (var (type, label) in channels)
            {
                var color = ChatColors.For(type);
                if (ImGui.ColorEdit4($"##color-{(ushort)type}", ref color,
                        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                {
                    config.ChannelColors[type] = PackRgba(color);
                    config.Save();
                    ChatColors.SetOverrides(config.ChannelColors);
                }

                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.For(type)))
                {
                    ImGui.TextUnformatted(label);
                }

                if (config.ChannelColors.ContainsKey(type))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"reset##{(ushort)type}"))
                    {
                        config.ChannelColors.Remove(type);
                        config.Save();
                        ChatColors.SetOverrides(config.ChannelColors);
                    }
                }
            }
        }
    }

    private static uint PackRgba(Vector4 color) =>
        (uint)(byte)(color.X * 255f + 0.5f)
        | ((uint)(byte)(color.Y * 255f + 0.5f) << 8)
        | ((uint)(byte)(color.Z * 255f + 0.5f) << 16)
        | 0xFF000000;

    private string historyQuery = string.Empty;
    private List<Services.MessageDatabase.SearchResult> historyResults = [];
    private DateTime historySearchDue = DateTime.MaxValue;

    /// <summary>Typing pause before the search runs — a LIKE '%q%' query is a
    /// full table scan; per-keystroke it would hitch the render thread.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private void DrawHistoryTab(Configuration config)
    {
        SectionHeader("Retention", first: true);
        DrawRetention(config);

        SectionHeader("Search");

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##history-search", "Search stored history…", ref historyQuery, 200))
            historySearchDue = DateTime.Now + SearchDebounce;

        if (DateTime.Now >= historySearchDue)
        {
            historySearchDue = DateTime.MaxValue;
            var query = historyQuery.Trim();
            historyResults = query.Length >= 2 ? plugin.Database.Search(query, 200) : [];
        }

        using var child = ImRaii.Child("##history-results", new Vector2(-1, -1), true);
        if (!child.Success)
            return;

        if (historyResults.Count == 0)
        {
            using var dim = ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim);
            ImGui.TextUnformatted(historyQuery.Trim().Length < 2
                ? "Type at least 2 characters."
                : "No matches in stored history.");
            return;
        }

        foreach (var result in historyResults)
        {
            var stamp = result.Timestamp.ToString(config.Use24HourClock ? "MM-dd HH:mm" : "MM-dd h:mm tt");
            using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
            {
                ImGui.TextUnformatted($"[{stamp}]");
            }

            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.For(Services.ChatTypes.Mask(result.Type))))
            {
                ImGui.TextWrapped(result.Sender.Length > 0
                    ? $"{result.Sender}: {result.Text}"
                    : result.Text);
            }
        }
    }

    private static readonly (int Days, string Label)[] RetentionChoices =
    [
        (0, "Session only"), (7, "7 days"), (30, "30 days"),
        (90, "90 days"), (365, "1 year"), (-1, "Forever"),
    ];

    private void DrawRetention(Configuration config)
    {
        var index = Array.FindIndex(RetentionChoices, c => c.Days == config.RetentionDays);
        if (index < 0)
            index = 2;

        ImGui.SetNextItemWidth(120f);
        if (ImGui.Combo("Keep history", ref index,
                Array.ConvertAll(RetentionChoices, c => c.Label), RetentionChoices.Length))
        {
            config.RetentionDays = RetentionChoices[index].Days;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Messages older than this are removed when the plugin loads.\n\"Session only\" starts fresh every launch.");

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.TextUnformatted(FormatSize(plugin.Database.SizeBytes()));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (float)(1 << 30):0.0} GB",
        >= 1 << 20 => $"{bytes / (float)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / (float)(1 << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    private static void SectionHeader(string label, bool first = false)
    {
        if (!first)
        {
            ImGui.Spacing();
            ImGui.Spacing();
        }

        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.GoldBright))
        {
            ImGui.TextUnformatted(label);
        }

        ImGui.Separator();
    }

    private void DrawTabEditor(Configuration config)
    {
        var pendingDelete = -1;

        for (var i = 0; i < config.Tabs.Count; i++)
        {
            var tab = config.Tabs[i];
            using var id = ImRaii.PushId(i);

            // "###" keeps the header's identity stable across renames.
            if (!ImGui.CollapsingHeader($"{tab.Name}###tabheader"))
                continue;

            using var indent = ImRaii.PushIndent();

            DrawTabName(config, tab);
            DrawTabSendCommand(config, tab);

            var unread = tab.NotifyUnread;
            if (ImGui.Checkbox("Unread badge", ref unread))
            {
                tab.NotifyUnread = unread;
                ApplyTabChanges(config);
            }

            ImGui.SameLine();
            var catchAll = tab.CatchAll;
            if (ImGui.Checkbox("Catch-all", ref catchAll))
            {
                tab.CatchAll = catchAll;
                ApplyTabChanges(config);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Also receives non-combat messages no other tab matched\n(join notices, obtain lines, unnamed system kinds).");

            DrawChannelGrid(config, tab);

            ImGui.Spacing();
            var ctrlHeld = ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(!ctrlHeld || config.Tabs.Count <= 1))
            {
                if (ImGui.Button("Delete tab"))
                    pendingDelete = i;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(config.Tabs.Count <= 1
                    ? "The last tab can't be deleted."
                    : "Hold Ctrl to enable. Chat history is kept.");
            }

            ImGui.Spacing();
        }

        if (pendingDelete >= 0)
        {
            var removed = config.Tabs[pendingDelete];
            config.Tabs.RemoveAt(pendingDelete);
            config.TabOrder.Remove("tab:" + removed.Name);
            EnsureCombineStillValid(config);
            ApplyTabChanges(config);
        }

        if (ImGui.Button("Add tab"))
        {
            config.Tabs.Add(new TabConfig { Name = UniqueTabName(config), Channels = [XivChatType.Say] });
            ApplyTabChanges(config);
        }
    }

    private void DrawTabName(Configuration config, TabConfig tab)
    {
        var name = tab.Name;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.InputText("Name", ref name, 48))
            tab.Name = name;

        // Activation fires before the first edit, so this captures the
        // pre-rename name the tab's saved order id is derived from.
        if (ImGui.IsItemActivated())
            renameOriginal = tab.Name;

        if (ImGui.IsItemDeactivatedAfterEdit())
            CommitRename(config, tab);
    }

    private void CommitRename(Configuration config, TabConfig tab)
    {
        var newName = tab.Name.Trim();

        // Names double as tab ids; empties and duplicates would collide.
        if (newName.Length == 0 || config.Tabs.Exists(t => t != tab && t.Name == newName))
        {
            tab.Name = renameOriginal;
            return;
        }

        tab.Name = newName;
        if (newName == renameOriginal)
            return;

        // Keep the tab's saved position under its new id.
        var orderIndex = config.TabOrder.IndexOf("tab:" + renameOriginal);
        if (orderIndex >= 0)
            config.TabOrder[orderIndex] = "tab:" + newName;

        EnsureCombineStillValid(config);
        ApplyTabChanges(config);
    }

    private void DrawTabSendCommand(Configuration config, TabConfig tab)
    {
        var send = tab.SendCommand ?? string.Empty;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.InputTextWithHint("Send command", "active channel", ref send, 32))
            tab.SendCommand = send.Length == 0 ? null : send;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            tab.SendCommand = NormalizeSendCommand(tab.SendCommand);
            ApplyTabChanges(config);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Plain text in this tab is sent through this chat command, e.g. /p.\nEmpty: sends to the game's currently active channel.");
    }

    /// <summary>First word, slash-prefixed; null when effectively empty.</summary>
    private static string? NormalizeSendCommand(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || text == "/")
            return null;

        var firstSpace = text.IndexOf(' ');
        if (firstSpace > 0)
            text = text[..firstSpace];

        return text[0] == '/' ? text : "/" + text;
    }

    private void DrawChannelGrid(Configuration config, TabConfig tab)
    {
        foreach (var (group, channels) in ChannelGroups)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
            {
                ImGui.TextUnformatted(group);
            }

            using var table = ImRaii.Table("##channels" + group, 3);
            if (!table.Success)
                continue;

            foreach (var (type, label) in channels)
            {
                ImGui.TableNextColumn();
                var enabled = tab.Channels.Contains(type);
                using var tint = ImRaii.PushColor(ImGuiCol.Text, ChatColors.For(type), enabled);
                if (ImGui.Checkbox(label, ref enabled))
                {
                    if (enabled)
                        tab.Channels.Add(type);
                    else
                        tab.Channels.Remove(type);
                    ApplyTabChanges(config);
                }
            }
        }
    }

    /// <summary>Renames/deletes can orphan the combined-All flag; drop it then.</summary>
    private static void EnsureCombineStillValid(Configuration config)
    {
        if (config.CombineGeneralSystem && !config.Tabs.Exists(t => t.Name is "General" or "System"))
            config.CombineGeneralSystem = false;
    }

    private static string UniqueTabName(Configuration config)
    {
        var name = "New Tab";
        for (var n = 2; config.Tabs.Exists(t => t.Name == name); n++)
            name = $"New Tab {n}";
        return name;
    }

    private void ApplyTabChanges(Configuration config)
    {
        config.Save();
        plugin.TabManager.RebuildFixedTabs();
    }
}
