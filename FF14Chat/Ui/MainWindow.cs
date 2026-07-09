using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Hooking;
using FF14Chat.Model;
using FF14Chat.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Chat.Ui;

public class MainWindow : Window, IDisposable
{
    // Rendering is not virtualized yet, so cap how much we lay out per frame.
    private const int MaxRenderedMessages = 500;

    private const int MaxHistory = 100;

    private readonly Plugin plugin;
    private readonly TabManager tabs;
    private readonly Dictionary<string, string> drafts = [];

    private readonly List<string> sentHistory = [];
    private int historyPos = -1;
    private string historyStash = string.Empty;
    private bool focusInput;

    private readonly CommandIndex commandIndex = new();
    private List<CommandEntry> suggestions = [];
    private int suggestionIndex;
    private string suggestionQuery = string.Empty;

    private string? selectTabId;

    private bool enterWasDown;
    private bool slashWasDown;
    private bool pendingSlash;
    private bool clearSelection;

    private IFontHandle gameFont;
    private bool vanillaHidden;
    private System.IDisposable? fontPush;
    private ImRaii.ColorDisposable? themeColors;
    private ImRaii.StyleDisposable? themeStyles;

    // Conditions in which Enter belongs to the game (advancing NPC dialogue,
    // cutscenes, occupied states), not to the chat window.
    private static readonly ConditionFlag[] EnterBlockedConditions =
    [
        ConditionFlag.Occupied,
        ConditionFlag.Occupied30,
        ConditionFlag.Occupied33,
        ConditionFlag.Occupied38,
        ConditionFlag.Occupied39,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.OccupiedSummoningBell,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
    ];

    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);

    public MainWindow(Plugin plugin, TabManager tabs) : base("FF14Chat###FF14ChatMain")
    {
        this.plugin = plugin;
        this.tabs = tabs;

        Plugin.Framework.Update += OnFrameworkUpdate;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(600, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags = BaseFlags;

        // Chat is a permanent fixture: open on load, immune to Escape.
        IsOpen = true;
        RespectCloseHotkey = false;

        gameFont = CreateFont();

        unsafe
        {
            setContextTellTargetHook = Plugin.GameInterop.HookFromAddress<SetContextTellTargetDelegate>(
                (nint)RaptureShellModule.MemberFunctionPointers.SetContextTellTarget,
                SetContextTellTargetDetour);
        }

        setContextTellTargetHook.Enable();
    }

    private unsafe delegate bool SetContextTellTargetDelegate(
        RaptureShellModule* module, Utf8String* playerName, Utf8String* worldName,
        ushort worldId, ulong contentId, ulong accountId, ushort reason, bool a8);

    private readonly Hook<SetContextTellTargetDelegate> setContextTellTargetHook;

    /// <summary>
    /// "Send Tell" from the game's context menus lands here. While we're the
    /// active chat, open our tell tab instead of the native chat input.
    /// </summary>
    private unsafe bool SetContextTellTargetDetour(
        RaptureShellModule* module, Utf8String* playerName, Utf8String* worldName,
        ushort worldId, ulong contentId, ulong accountId, ushort reason, bool a8)
    {
        try
        {
            if (IsOpen && plugin.Configuration.HideVanillaChat && playerName != null)
            {
                var name = playerName->ToString();
                var world = worldName != null ? worldName->ToString() : string.Empty;
                if (world.Length == 0
                    && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().TryGetRow(worldId, out var worldRow))
                {
                    world = worldRow.Name.ExtractText();
                }

                if (name.Length > 0)
                {
                    var tellTab = tabs.OpenTellTab(world.Length > 0 ? $"{name}@{world}" : name);
                    selectTabId = tellTab.Id;
                    focusInput = true;
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "SetContextTellTarget detour failed");
        }

        return setContextTellTargetHook.Original(module, playerName, worldName, worldId, contentId, accountId, reason, a8);
    }

    /// <summary>
    /// The game's own UI font. Must be one of the game's native bitmap sizes
    /// or it scales and blurs.
    /// </summary>
    private IFontHandle CreateFont()
    {
        var family = plugin.Configuration.FontSize switch
        {
            <= 10 => GameFontFamilyAndSize.Axis96,
            <= 12 => GameFontFamilyAndSize.Axis12,
            <= 14 => GameFontFamilyAndSize.Axis14,
            _ => GameFontFamilyAndSize.Axis18,
        };
        return Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(family));
    }

    /// <summary>Called by settings when the font size changes.</summary>
    public void RebuildFont()
    {
        gameFont.Dispose();
        gameFont = CreateFont();
    }

    /// <summary>Mirror vanilla chat visibility rules: only in the world, UI shown, no cutscene.</summary>
    public override bool DrawConditions()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return false;
        if (Plugin.GameGui.GameUiHidden)
            return false;
        if (Plugin.Condition[ConditionFlag.WatchingCutscene]
            || Plugin.Condition[ConditionFlag.WatchingCutscene78]
            || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Plugin.Condition[ConditionFlag.CreatingCharacter])
        {
            return false;
        }

        return true;
    }

    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        setContextTellTargetHook.Dispose();
        SetVanillaChatVisible(true);
        gameFont.Dispose();
    }

    public override void PreDraw()
    {
        Flags = plugin.Configuration.LockWindow
            ? BaseFlags | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            : BaseFlags;

        FFTheme.Configure(plugin.Configuration);
        themeColors = FFTheme.PushColors();
        themeStyles = FFTheme.PushStyles();
        if (gameFont.Available)
            fontPush = gameFont.Push();
    }

    public override void PostDraw()
    {
        fontPush?.Dispose();
        fontPush = null;
        themeStyles?.Dispose();
        themeStyles = null;
        themeColors?.Dispose();
        themeColors = null;
    }

    /// <summary>
    /// Runs before the game processes input for the tick, so consuming the
    /// key here keeps the vanilla chat box from opening. Render-time checks
    /// are too late: the game has already reacted to the key by then.
    /// </summary>
    private static readonly string[] VanillaChatAddons =
        ["ChatLog", "ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];

    private unsafe void SetVanillaChatVisible(bool visible)
    {
        foreach (var name in VanillaChatAddons)
        {
            var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
            if (addon != null && addon->IsVisible != visible)
                addon->IsVisible = visible;
        }
    }

    /// <summary>
    /// True while the game's own chat input has keyboard focus. Happens even
    /// with the chat hidden: the game opens it for reply flows and the '/'
    /// character shortcut, and it would silently swallow all typing.
    /// </summary>
    private static unsafe bool IsVanillaChatInputFocused()
    {
        var atkModule = RaptureAtkModule.Instance();
        if (atkModule == null || !atkModule->AtkModule.IsTextInputActive())
            return false;

        var unitManager = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager;
        foreach (var entry in unitManager->FocusedUnitsList.Entries)
        {
            var unit = entry.Value;
            if (unit != null && unit->NameString == "ChatLog")
                return true;
        }

        return false;
    }

    private unsafe void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        // Hide vanilla chat while we're replacing it; restore it exactly once
        // when we stop, so cutscene/logout visibility stays the game's call.
        // While the game's own chat input is focused (reply flows, the '/'
        // char shortcut), show vanilla chat so typing is visible instead of
        // going into an invisible box.
        var shouldHide = IsOpen && plugin.Configuration.HideVanillaChat && Plugin.ClientState.IsLoggedIn
                         && !IsVanillaChatInputFocused();
        if (shouldHide)
        {
            SetVanillaChatVisible(false);
            vanillaHidden = true;
        }
        else if (vanillaHidden)
        {
            SetVanillaChatVisible(true);
            vanillaHidden = false;
        }

        if (!Plugin.ClientState.IsLoggedIn)
            return;

        var enterDown = Plugin.KeyState[VirtualKey.RETURN];
        var enterPressed = enterDown && !enterWasDown;
        enterWasDown = enterDown;

        var (slashKey, slashNeedsShift) = SlashKey();
        var slashDown = slashKey != VirtualKey.NO_KEY && Plugin.KeyState[slashKey];
        var slashPressed = slashDown && !slashWasDown;
        slashWasDown = slashDown;

        if (!IsOpen || (!enterPressed && !slashPressed))
            return;

        if (ImGui.GetIO().WantTextInput)
            return;

        var atkModule = RaptureAtkModule.Instance();
        if (atkModule != null && atkModule->AtkModule.IsTextInputActive())
            return;

        if (enterPressed)
        {
            foreach (var flag in EnterBlockedConditions)
            {
                if (Plugin.Condition[flag])
                    return;
            }

            Plugin.KeyState[VirtualKey.RETURN] = false;
            focusInput = true;
            return;
        }

        // Slash: only when the layout's shift requirement matches what's held.
        if (Plugin.KeyState[VirtualKey.SHIFT] != slashNeedsShift)
            return;

        Plugin.KeyState[slashKey] = false;
        focusInput = true;
        pendingSlash = true;
    }

    /// <summary>Resolves which physical key produces '/' on the current keyboard layout.</summary>
    private static (VirtualKey Key, bool NeedsShift) SlashKey()
    {
        var scan = VkKeyScanW('/');
        if (scan == -1)
            return (VirtualKey.NO_KEY, false);
        return ((VirtualKey)(scan & 0xFF), (scan & 0x100) != 0);
    }

    public override void Draw()
    {
        if (!plugin.Configuration.PlacedAtVanillaChat)
            TryPlaceAtVanillaChat();

        FFTheme.DrawWindowBackground();
        DrawHeader();

        // Submitted before the tab bar so the arrows win clicks over tabs
        // beneath them (earliest overlapping ImGui item gets the click).
        TabScrollArrowInputs();

        if (focusInput)
            ImGui.SetWindowFocus();

        // FittingPolicyScroll: overflowing tabs scroll horizontally instead of
        // shrinking. The native scroll arrows can't show a disabled state, so
        // they're hidden and we draw our own (see DrawTabScrollControls).
        using var tabBar = ImRaii.TabBar(
            "##tabs",
            ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll
            | ImGuiTabBarFlags.NoTabListScrollingButtons);
        if (!tabBar.Success)
            return;

        imguiIdToTabId.Clear();
        foreach (var tab in tabs.Snapshot())
        {
            // Constant label (badge drawn as an overlay) so tab widths never
            // jump when unread counts appear and disappear. The trailing
            // spaces reserve room for the badge.
            var label = $"{tab.Title}  ###{tab.Id}";
            var itemFlags = selectTabId == tab.Id ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

            if (tab.IsTell)
            {
                var open = true;
                using (var item = ImRaii.TabItem(label, ref open, itemFlags))
                {
                    imguiIdToTabId[ImGuiP.GetItemID()] = tab.Id;
                    DrawUnreadBadge(tab);
                    if (item.Success)
                        DrawTab(tab);
                }

                if (!open)
                    tabs.Close(tab);
            }
            else
            {
                using var item = ImRaii.TabItem(label);
                imguiIdToTabId[ImGuiP.GetItemID()] = tab.Id;
                DrawUnreadBadge(tab);
                if (item.Success)
                    DrawTab(tab);
            }
        }

        SyncTabOrder();
        UpdateTabScroll();
        DrawTabScrollArrowVisuals();
        selectTabId = null;
    }

    // Tab strip scroll state, captured inside the tab bar scope each frame
    // and used by the arrow inputs submitted before it next frame.
    private Vector2 tabStripMin;
    private Vector2 tabStripMax;
    private float tabScrollCurrent;
    private float tabScrollMax;
    private float pendingTabScroll;
    private bool leftArrowHovered;
    private bool rightArrowHovered;

    private float TabArrowSize => tabStripMax.Y - tabStripMin.Y;

    private bool LeftArrowEnabled => tabScrollCurrent > 0.5f;
    private bool RightArrowEnabled => tabScrollCurrent < tabScrollMax - 0.5f;

    private Vector2 LeftArrowPos => tabStripMin;
    private Vector2 RightArrowPos => new(tabStripMax.X - TabArrowSize, tabStripMin.Y);

    /// <summary>Invisible click-catchers for the scroll arrows.</summary>
    private void TabScrollArrowInputs()
    {
        if (tabScrollMax <= 0f || TabArrowSize <= 0f)
            return;

        leftArrowHovered = TabArrowButton("##tabscroll-l", LeftArrowPos, LeftArrowEnabled, -120f);
        rightArrowHovered = TabArrowButton("##tabscroll-r", RightArrowPos, RightArrowEnabled, 120f);
    }

    private bool TabArrowButton(string id, Vector2 pos, bool enabled, float delta)
    {
        var restore = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(pos);
        var clicked = ImGui.InvisibleButton(id, new Vector2(TabArrowSize, TabArrowSize));
        var hovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(restore);

        if (enabled && clicked)
            pendingTabScroll += delta;

        return hovered;
    }

    /// <summary>Applies wheel/arrow scrolling and refreshes the strip state. Tab bar scope only.</summary>
    private void UpdateTabScroll()
    {
        var bar = ImGui.GetCurrentContext().CurrentTabBar;
        if (bar.IsNull)
            return;

        tabStripMin = bar.BarRect.Min;
        tabStripMax = bar.BarRect.Max;
        tabScrollMax = Math.Max(0f, bar.WidthAllTabs - (tabStripMax.X - tabStripMin.X));

        var mouse = ImGui.GetMousePos();
        var overBar = ImGui.IsWindowHovered()
                      && mouse.X >= tabStripMin.X && mouse.X <= tabStripMax.X
                      && mouse.Y >= tabStripMin.Y && mouse.Y <= tabStripMax.Y;
        var wheel = ImGui.GetIO().MouseWheel;
        if (overBar && wheel != 0f)
            pendingTabScroll -= wheel * 80f;

        if (pendingTabScroll != 0f)
        {
            bar.ScrollingTarget = Math.Clamp(bar.ScrollingTarget + pendingTabScroll, 0f, tabScrollMax);
            pendingTabScroll = 0f;
        }

        tabScrollCurrent = bar.ScrollingTarget;
    }

    private void DrawTabScrollArrowVisuals()
    {
        if (tabScrollMax <= 0f || TabArrowSize <= 0f)
            return;

        DrawTabArrow(LeftArrowPos, left: true, LeftArrowEnabled, leftArrowHovered);
        DrawTabArrow(RightArrowPos, left: false, RightArrowEnabled, rightArrowHovered);
    }

    private void DrawTabArrow(Vector2 pos, bool left, bool enabled, bool hovered)
    {
        var size = TabArrowSize;
        var drawList = ImGui.GetWindowDrawList();
        // Backing so the arrow reads over the tabs scrolling beneath it.
        drawList.AddRectFilled(pos, pos + new Vector2(size, size), ImGui.GetColorU32(FFTheme.BgBottom with { W = 0.85f }), 2f);

        var color = !enabled
            ? FFTheme.TextDim with { W = 0.35f }
            : hovered ? FFTheme.GoldBright : FFTheme.Gold;
        var center = pos + new Vector2(size / 2f, size / 2f);
        var arm = size * 0.22f;
        var dir = left ? -1f : 1f;
        drawList.AddTriangleFilled(
            center + new Vector2(dir * arm, 0),
            center + new Vector2(-dir * arm * 0.6f, -arm),
            center + new Vector2(-dir * arm * 0.6f, arm),
            ImGui.GetColorU32(color));
    }

    private readonly Dictionary<uint, string> imguiIdToTabId = [];
    private readonly List<string> orderScratch = [];

    /// <summary>
    /// ImGui owns drag-reordering (its order is runtime-only), so read the
    /// display order back from the tab bar's internal state and persist it.
    /// </summary>
    private void SyncTabOrder()
    {
        // Note: ImGuiP.ImGuiTabBar() CONSTRUCTS a tab bar; the live one hangs
        // off the context. Only valid inside Begin/EndTabBar.
        var bar = ImGui.GetCurrentContext().CurrentTabBar;
        if (bar.IsNull)
            return;

        orderScratch.Clear();
        for (var i = 0; i < bar.Tabs.Size; i++)
        {
            if (imguiIdToTabId.TryGetValue(bar.Tabs[i].ID, out var tabId))
                orderScratch.Add(tabId);
        }

        tabs.SetOrder(orderScratch);
    }

    /// <summary>Places the window over the vanilla chat log, once, on first load.</summary>
    private void TryPlaceAtVanillaChat()
    {
        var addon = Plugin.GameGui.GetAddonByName("ChatLog");
        if (addon.IsNull || !addon.IsReady)
            return; // Not available yet (login screen); retry next frame.

        ImGui.SetWindowPos(addon.Position);
        ImGui.SetWindowSize(Vector2.Max(addon.ScaledSize, new Vector2(400, 250)));

        plugin.Configuration.PlacedAtVanillaChat = true;
        plugin.Configuration.Save();
    }

    /// <summary>Title row: drag area, gold title, lock and close buttons, fading gold rule.</summary>
    private void DrawHeader()
    {
        var width = ImGui.GetWindowWidth();
        var padding = ImGui.GetStyle().WindowPadding.X;
        var lineHeight = ImGui.GetTextLineHeight();
        var headerHeight = lineHeight + 4f;
        var start = ImGui.GetCursorPos();
        var screenStart = ImGui.GetCursorScreenPos();

        // Drag anywhere on the header (the window has no native title bar).
        // Must stop short of the gear/lock/close buttons: an overlapping
        // earlier button steals their clicks.
        ImGui.InvisibleButton("##header-drag", new Vector2(width - padding * 2 - 76f, headerHeight));
        if (!plugin.Configuration.LockWindow
            && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }

        ImGui.SetCursorPos(start + new Vector2(2f, 2f));
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.GoldBright))
        {
            ImGui.TextUnformatted("Chat");
        }

        DrawGearButton(new Vector2(width - padding - 66f, start.Y + 2f));
        DrawLockButton(new Vector2(width - padding - 42f, start.Y + 2f));
        DrawCloseButton(new Vector2(width - padding - 18f, start.Y + 2f));

        FFTheme.DrawFadingSeparator(
            screenStart + new Vector2(0, headerHeight + 3f),
            width - padding * 2);

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + headerHeight + 8f));
    }

    private void DrawGearButton(Vector2 cursorPos)
    {
        ImGui.SetCursorPos(cursorPos);
        var size = ImGui.GetTextLineHeight();
        if (ImGui.InvisibleButton("##settings", new Vector2(size, size)))
            plugin.ToggleConfigUi();

        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip("Settings");

        var min = ImGui.GetItemRectMin();
        var center = min + new Vector2(size / 2f, size / 2f);
        var radius = size * 0.27f;
        var color = ImGui.GetColorU32(hovered ? FFTheme.GoldBright : FFTheme.TextDim);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddCircle(center, radius, color, 12, 1.6f);
        for (var i = 0; i < 8; i++)
        {
            var angle = MathF.Tau * i / 8f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            drawList.AddLine(center + dir * radius, center + dir * (radius + size * 0.14f), color, 1.6f);
        }
    }

    private void DrawLockButton(Vector2 cursorPos)
    {
        ImGui.SetCursorPos(cursorPos);
        var size = ImGui.GetTextLineHeight();
        var locked = plugin.Configuration.LockWindow;

        if (ImGui.InvisibleButton("##lock", new Vector2(size, size)))
        {
            plugin.Configuration.LockWindow = !locked;
            plugin.Configuration.Save();
        }

        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(locked ? "Unlock window" : "Lock window (position and size)");

        var min = ImGui.GetItemRectMin();
        var scale = size / 16f;
        var color = ImGui.GetColorU32(hovered ? FFTheme.GoldBright : locked ? FFTheme.Gold : FFTheme.TextDim);
        var drawList = ImGui.GetWindowDrawList();

        var centerX = min.X + size / 2f;
        var bodyTop = min.Y + size * 0.48f;

        // Body.
        drawList.AddRectFilled(
            new Vector2(centerX - 4.5f * scale, bodyTop),
            new Vector2(centerX + 4.5f * scale, bodyTop + 6.5f * scale),
            color, 1.5f * scale);

        // Shackle: full arc when locked, tilted open arc otherwise.
        if (locked)
        {
            drawList.PathArcTo(new Vector2(centerX, bodyTop), 3f * scale, MathF.PI, MathF.Tau);
        }
        else
        {
            drawList.PathArcTo(new Vector2(centerX + 1.5f * scale, bodyTop - 0.5f * scale), 3f * scale, MathF.PI * 0.95f, MathF.PI * 1.75f);
        }

        drawList.PathStroke(color, ImDrawFlags.None, 1.6f * scale);
    }

    private void DrawCloseButton(Vector2 cursorPos)
    {
        ImGui.SetCursorPos(cursorPos);
        var size = ImGui.GetTextLineHeight();
        if (ImGui.InvisibleButton("##close", new Vector2(size, size)))
            IsOpen = false;

        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var inset = size * 0.26f;
        var color = ImGui.GetColorU32(hovered ? FFTheme.GoldBright : FFTheme.TextDim);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(min + new Vector2(inset, inset), max - new Vector2(inset, inset), color, 1.5f);
        drawList.AddLine(new Vector2(max.X - inset, min.Y + inset), new Vector2(min.X + inset, max.Y - inset), color, 1.5f);
    }

    /// <summary>Draws a count bubble over the tab header (the last ImGui item).</summary>
    private static void DrawUnreadBadge(TabState tab)
    {
        if (tab.Unread <= 0)
            return;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        var radius = ImGui.GetFontSize() * 0.42f;
        var center = new Vector2(max.X - radius - 2, min.Y + radius + 1);
        var text = tab.Unread > 9 ? "9+" : tab.Unread.ToString();

        drawList.AddCircleFilled(center, radius + 2, ImGui.GetColorU32(new Vector4(0.80f, 0.20f, 0.20f, 1f)));

        var scale = 0.75f;
        var size = ImGui.CalcTextSize(text) * scale;
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, center - size / 2, 0xFFFFFFFF, text);
    }

    private void DrawTab(TabState tab)
    {
        tabs.MarkRead(tab);

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2)))
        using (var child = ImRaii.Child("##log", new Vector2(-1, -ImGui.GetFrameHeightWithSpacing()), false))
        {
            if (child.Success)
                DrawLog(tab);
        }

        DrawInput(tab);
    }

    private void DrawLog(TabState tab)
    {
        var messages = tabs.MessagesSnapshot(tab);
        // First draw of a tab (e.g. history just hydrated) starts pinned.
        var firstDraw = tab.RenderedRevision == -1;
        var pinnedToBottom = firstDraw || ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
        var newMessages = firstDraw || tab.Revision != tab.RenderedRevision;
        tab.RenderedRevision = tab.Revision;

        if (messages.Length == 0)
        {
            using var dim = ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp);
            ImGui.TextWrapped("No messages yet — chat will appear here as it happens.");
            return;
        }

        var first = Math.Max(0, messages.Length - MaxRenderedMessages);
        for (var i = first; i < messages.Length; i++)
            DrawMessage(messages[i]);

        if (pinnedToBottom && newMessages)
            ImGui.SetScrollHereY(1f);
    }

    private bool inputActiveLastFrame;
    private TabState? inputTab;

    // Say -> Party -> Alliance -> FC, the useful everyday rotation.
    private static readonly int[] ChannelCycle = [1, 2, 3, 6];

    /// <summary>Switches the game's active input channel to the next in the cycle.</summary>
    private unsafe void CycleGameChannel()
    {
        var agent = AgentChatLog.Instance();
        var shell = RaptureShellModule.Instance();
        if (agent == null || shell == null)
            return;

        var index = Array.IndexOf(ChannelCycle, (int)agent->CurrentChannel);
        var next = ChannelCycle[(index + 1) % ChannelCycle.Length];

        var empty = Utf8String.FromString(string.Empty);
        try
        {
            shell->ChangeChatChannel(next, 0, empty, true);
        }
        finally
        {
            empty->Dtor(true);
        }
    }

    private void SwitchToNextTab(TabState current)
    {
        var all = tabs.Snapshot();
        if (all.Length < 2)
            return;

        var index = Array.FindIndex(all, t => t.Id == current.Id);
        selectTabId = all[(index + 1 + all.Length) % all.Length].Id;
        focusInput = true;
    }

    /// <summary>
    /// The game's currently active input channel (what an untargeted message
    /// would go to), as color + localized label from the chat log agent.
    /// The numbering follows the game's input channels; ClientStructs only
    /// names the first few, verified against Say/Party/Alliance.
    /// </summary>
    private static unsafe (Vector4 Color, string Label)? ActiveGameChannel()
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return null;

        var type = (int)agent->CurrentChannel switch
        {
            0 => XivChatType.TellOutgoing,
            1 => XivChatType.Say,
            2 => XivChatType.Party,
            3 => XivChatType.Alliance,
            4 => XivChatType.Yell,
            5 => XivChatType.Shout,
            6 => XivChatType.FreeCompany,
            7 => XivChatType.PvPTeam,
            8 => XivChatType.NoviceNetwork,
            // XivChatType linkshell values aren't contiguous (CWLS2+ live in a
            // different range), but they all share one color anyway.
            >= 9 and <= 16 => XivChatType.CrossLinkShell1,
            >= 19 and <= 26 => XivChatType.Ls1,
            _ => XivChatType.Say,
        };

        return (ChatColors.For(type), agent->ChannelLabel.ToString());
    }

    /// <summary>Destination color + label for plain text typed in this tab.</summary>
    private static (Vector4 Color, string Label)? SendDestination(TabState tab)
    {
        if (tab.IsTell)
            return (ChatColors.For(XivChatType.TellOutgoing), $"Tell {tab.Title}");

        var fixedType = tab.SendCommand switch
        {
            "/p" or "/party" => XivChatType.Party,
            "/fc" or "/freecompany" => XivChatType.FreeCompany,
            "/s" or "/say" => XivChatType.Say,
            "/sh" or "/shout" => XivChatType.Shout,
            "/y" or "/yell" => XivChatType.Yell,
            "/a" or "/alliance" => XivChatType.Alliance,
            "/n" or "/novice" => XivChatType.NoviceNetwork,
            _ => (XivChatType?)null,
        };

        if (fixedType is { } type)
            return (ChatColors.For(type), tab.SendCommand!);

        // No per-tab channel: plain text goes to the game's active channel.
        return ActiveGameChannel();
    }

    private void DrawInput(TabState tab)
    {
        inputTab = tab;
        drafts.TryGetValue(tab.Id, out var draft);
        draft ??= string.Empty;

        // While the field is focused, tint its border with the channel color
        // the message will be sent in, as a destination indicator.
        var destination = SendDestination(tab);
        using var border = ImRaii.PushColor(
            ImGuiCol.Border, destination?.Color ?? default, inputActiveLastFrame && destination.HasValue);

        if (focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusInput = false;
            clearSelection = true;

            if (pendingSlash)
            {
                pendingSlash = false;
                if (draft.Length == 0)
                    draft = "/";
            }
        }

        var hint = tab.IsTell
            ? $"Message {tab.Title}…"
            : destination is { Label.Length: > 0 } dest
                ? $"{dest.Label}…"
                : "Chat or /command…";
        var inputPos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint(
            $"##input{tab.Id}", hint, ref draft, 500,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory
            | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion,
            InputCallback);
        var inputActive = ImGui.IsItemActive();
        inputActiveLastFrame = inputActive;
        drafts[tab.Id] = draft;

        UpdateSuggestions(draft, inputActive || focusInput);
        if (suggestions.Count > 0)
            DrawSuggestions(tab, inputPos);

        if (!submitted)
            return;

        if (Submit(tab, draft))
            drafts[tab.Id] = string.Empty;

        // Keep typing without re-clicking the field.
        ImGui.SetKeyboardFocusHere(-1);
    }

    private void UpdateSuggestions(string draft, bool inputActive)
    {
        var wantSuggestions = inputActive
                              && draft.Length > 1
                              && draft[0] == '/'
                              && !draft.Contains(' ');
        if (!wantSuggestions)
        {
            suggestions = [];
            suggestionQuery = string.Empty;
            return;
        }

        if (draft != suggestionQuery)
        {
            suggestionQuery = draft;
            suggestions = commandIndex.Query(draft);
            suggestionIndex = 0;
        }
    }

    private void DrawSuggestions(TabState tab, Vector2 inputPos)
    {
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = suggestions.Count * lineHeight + ImGui.GetStyle().WindowPadding.Y * 2;
        var width = Math.Max(320f, ImGui.GetWindowWidth() * 0.6f);

        ImGui.SetNextWindowPos(new Vector2(inputPos.X, inputPos.Y - height - 4));
        ImGui.SetNextWindowSize(new Vector2(width, height));

        // The Tooltip flag moves the window to the tooltip z-layer so it always
        // draws above the chat window; unlike BeginTooltip it stays clickable.
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.Tooltip;

        // Drawn as an overlay window so it can float above the input.
        if (!ImGui.Begin("##ff14chat-suggestions", flags))
        {
            ImGui.End();
            return;
        }

        for (var i = 0; i < suggestions.Count; i++)
        {
            var entry = suggestions[i];
            var selected = i == suggestionIndex;
            if (ImGui.Selectable($"{entry.Command}##sugg{i}", selected))
            {
                drafts[tab.Id] = entry.Command + " ";
                focusInput = true;
            }

            if (entry.Description.Length > 0)
            {
                ImGui.SameLine();
                using var dim = ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp);
                ImGui.TextUnformatted(entry.Description);
            }
        }

        ImGui.End();
    }

    private int InputCallback(ImGuiInputTextCallbackDataPtr data)
    {
        if (data.EventFlag == ImGuiInputTextFlags.CallbackAlways)
        {
            // Programmatic focus selects the whole buffer; typing would then
            // replace it. Put the cursor at the end with nothing selected.
            if (clearSelection)
            {
                clearSelection = false;
                data.CursorPos = data.BufTextLen;
                data.SelectionStart = data.BufTextLen;
                data.SelectionEnd = data.BufTextLen;
            }

            return 0;
        }

        // Tab: with text, accept the highlighted suggestion; on an empty
        // input, cycle chats (fixed-destination tabs) or the game's active
        // channel (General/System), which updates the border indicator.
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (data.BufTextLen == 0)
            {
                if (inputTab is { } current && (current.IsTell || current.SendCommand is { Length: > 0 }))
                    SwitchToNextTab(current);
                else
                    CycleGameChannel();

                return 0;
            }

            if (suggestions.Count > 0)
            {
                var completed = suggestions[Math.Clamp(suggestionIndex, 0, suggestions.Count - 1)].Command + " ";
                data.DeleteChars(0, data.BufTextLen);
                data.InsertChars(0, completed);
            }

            return 0;
        }

        if (data.EventFlag != ImGuiInputTextFlags.CallbackHistory)
            return 0;

        // While suggestions are open, up/down moves the highlight, not history.
        if (suggestions.Count > 0)
        {
            if (data.EventKey == ImGuiKey.UpArrow)
                suggestionIndex = (suggestionIndex - 1 + suggestions.Count) % suggestions.Count;
            else if (data.EventKey == ImGuiKey.DownArrow)
                suggestionIndex = (suggestionIndex + 1) % suggestions.Count;
            return 0;
        }

        if (sentHistory.Count == 0)
            return 0;

        int newPos;
        if (data.EventKey == ImGuiKey.UpArrow)
        {
            if (historyPos == -1)
            {
                // Entering history: stash whatever is being typed.
                historyStash = System.Text.Encoding.UTF8.GetString(data.BufTextSpan);
                newPos = sentHistory.Count - 1;
            }
            else
            {
                newPos = Math.Max(0, historyPos - 1);
            }
        }
        else if (data.EventKey == ImGuiKey.DownArrow)
        {
            if (historyPos == -1)
                return 0;
            newPos = historyPos + 1 >= sentHistory.Count ? -1 : historyPos + 1;
        }
        else
        {
            return 0;
        }

        historyPos = newPos;
        var replacement = newPos == -1 ? historyStash : sentHistory[newPos];
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, replacement);
        return 0;
    }

    private bool Submit(TabState tab, string draft)
    {
        var text = draft.Trim();
        if (text.Length == 0)
            return true;

        var toSend = text[0] == '/'
            ? text
            : tab.IsTell
                ? $"/tell {tab.TellPartner} {text}"
                : tab.SendCommand is { Length: > 0 } sendCommand
                    ? $"{sendCommand} {text}"
                    : text;

        if (!ChatSender.Send(toSend))
            return false;

        if (sentHistory.Count == 0 || sentHistory[^1] != text)
        {
            sentHistory.Add(text);
            if (sentHistory.Count > MaxHistory)
                sentHistory.RemoveAt(0);
        }

        historyPos = -1;
        historyStash = string.Empty;
        return true;
    }

    private void DrawMessage(Message message)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted($"[{message.Timestamp:HH:mm}]");
        }

        var channelColor = ChatColors.For(message.Type);

        var prefix = FormatPrefix(message);
        if (prefix.Length > 0)
        {
            var senderLink = message.SenderPlayer != null
                ? new SegmentLink.Player(message.SenderPlayer)
                : null;
            DrawSegmentText(prefix + " ", channelColor, senderLink);
        }

        if (message.Segments.Count > 0)
        {
            foreach (var segment in message.Segments)
                DrawSegmentText(segment.Text, segment.Color ?? channelColor, segment.Link);
        }
        else
        {
            DrawSegmentText(message.Text, channelColor, null);
        }
    }

    /// <summary>
    /// Draws text continuing the current chat line, word-wrapping against the
    /// window edge. Assumes the previous ImGui item is the preceding chunk of
    /// this same line (the timestamp starts every line).
    /// </summary>
    private void DrawSegmentText(string text, Vector4 color, SegmentLink? link)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var lines = text.Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var forceNewLine = li > 0;
            foreach (var token in Tokenize(lines[li]))
            {
                DrawToken(token, link, forceNewLine);
                forceNewLine = false;
            }
        }
    }

    private void DrawToken(string token, SegmentLink? link, bool forceNewLine)
    {
        if (!forceNewLine)
        {
            var tokenWidth = ImGui.CalcTextSize(token).X;
            var lastEnd = ImGui.GetItemRectMax().X;
            var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            if (lastEnd + tokenWidth <= rightEdge)
                ImGui.SameLine(0, 0);
        }

        ImGui.TextUnformatted(token);

        if (link == null || !ImGui.IsItemHovered())
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        switch (link)
        {
            case SegmentLink.Item item:
                DrawItemTooltip(item);
                if (clicked)
                {
                    var name = item.Name ?? $"Item #{item.ItemId}";
                    ImGui.SetClipboardText(name);
                    Notify($"Copied \"{name}\"");
                }

                break;

            case SegmentLink.Map map:
                ImGui.SetTooltip("Click: open map");
                if (clicked)
                    Plugin.GameGui.OpenMapWithMapLink(map.Payload);
                break;

            case SegmentLink.Player player:
                ImGui.SetTooltip($"{player.Partner}\nClick: open tell tab");
                if (clicked)
                {
                    var tellTab = tabs.OpenTellTab(player.Partner);
                    selectTabId = tellTab.Id;
                    focusInput = true;
                }

                break;
        }
    }

    private static Vector4 RarityColor(byte rarity) => rarity switch
    {
        2 => new Vector4(0.55f, 0.95f, 0.55f, 1f), // green
        3 => new Vector4(0.45f, 0.65f, 1.00f, 1f), // blue
        4 => new Vector4(0.75f, 0.55f, 0.95f, 1f), // purple
        7 => new Vector4(0.95f, 0.60f, 0.75f, 1f), // pink (relic)
        _ => new Vector4(0.95f, 0.95f, 0.95f, 1f),
    };

    private static void DrawItemTooltip(SegmentLink.Item link)
    {
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(link.ItemId, out var item))
        {
            ImGui.SetTooltip($"{link.Name ?? $"Item #{link.ItemId}"}\nClick: copy name");
            return;
        }

        using var tooltip = ImRaii.Tooltip();

        var icon = Plugin.TextureProvider
            .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.Icon, link.Hq))
            .GetWrapOrEmpty();
        ImGui.Image(icon.Handle, new Vector2(40, 40));
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RarityColor(item.Rarity)))
            {
                ImGui.TextUnformatted($"{item.Name.ExtractText()}{(link.Hq ? " " : "")}");
            }

            var category = item.ItemUICategory.ValueNullable?.Name.ExtractText() ?? "";
            using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
            {
                ImGui.TextUnformatted($"{category}  ·  Item Level {item.LevelItem.RowId}");
                if (item.LevelEquip > 1)
                    ImGui.TextUnformatted($"Equip Level {item.LevelEquip}");
            }
        }

        var description = item.Description.ExtractText();
        if (description.Length > 0)
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 320);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted("Click: copy name");
        }
    }

    private static void Notify(string content)
    {
        Plugin.Notifications.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
        {
            Content = content,
            Minimized = true,
        });
    }

    /// <summary>Splits a line into words, each keeping its trailing spaces.</summary>
    private static IEnumerable<string> Tokenize(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var space = line.IndexOf(' ', i);
            if (space < 0)
            {
                yield return line[i..];
                break;
            }

            var end = space;
            while (end < line.Length && line[end] == ' ')
                end++;
            yield return line[i..end];
            i = end;
        }
    }

    private static string FormatPrefix(Message message)
    {
        if (message.Sender.Length == 0)
            return string.Empty;

        return message.Type switch
        {
            XivChatType.TellIncoming => $"{message.Sender} >>",
            XivChatType.TellOutgoing => $">> {message.Sender}:",
            XivChatType.Party or XivChatType.CrossParty => $"({message.Sender})",
            XivChatType.Alliance => $"(({message.Sender}))",
            // Emote messages already contain the player's name in the text.
            XivChatType.CustomEmote or XivChatType.StandardEmote => string.Empty,
            XivChatType.FreeCompany => $"[FC]<{message.Sender}>",
            _ => $"{message.Sender}:",
        };
    }
}
