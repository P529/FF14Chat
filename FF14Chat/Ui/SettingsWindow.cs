using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FF14Chat.Ui;

public class SettingsWindow : Window, IDisposable
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

    private readonly Plugin plugin;
    private readonly MainWindow mainWindow;

    /// <summary>Tab name at the moment its rename field gained focus.</summary>
    private string renameOriginal = string.Empty;

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

        var tellPresence = config.ShowTellPresence;
        if (ImGui.Checkbox("Online status dot on tell tabs", ref tellPresence))
        {
            config.ShowTellPresence = tellPresence;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Green: online, red: AFK, gray: offline, blue: unknown.\nTracked via friend list, party, and nearby players;\nnon-friends who are elsewhere can't be looked up (blue).");

        var mentions = config.HighlightMentions;
        if (ImGui.Checkbox("Highlight messages mentioning you", ref mentions))
        {
            config.HighlightMentions = mentions;
            config.Save();
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
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Tabs"))
            DrawTabEditor(config);

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.TextUnformatted("History: last 30 days, restored on login.");
        }
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
