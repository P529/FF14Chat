using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FF14Chat.Model;
using FF14Chat.Services;
using FF14Chat.Services.Translation;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Chat.Ui;

/// <summary>
/// The chat window itself: lifecycle, the framework-update poll, the tab bar
/// and its scroll strip, the log, the context menus and the window chrome.
/// The rest lives beside it — MainWindow.Hooks.cs for the native detours,
/// MainWindow.Input.cs for the input row and completion, MainWindow.Render.cs
/// for turning messages into pixels.
/// </summary>
public partial class MainWindow : Window, IDisposable
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

    /// <summary>Tab the current history walk belongs to; a switch resets it.</summary>
    private string historyTabId = string.Empty;
    private bool focusInput;
    private bool suppressEnterUntilReleased;

    /// <summary>Set by a send; the log jumps to the newest line next frame.</summary>
    private bool scrollLogToBottom;

    // A send that needs its text translated first can't block the draw thread,
    // so it finishes on a continuation. Only one runs at a time (see Submit);
    // the tab it belongs to drives the input's "translating" hint.
    private Task? pendingSend;
    private string? pendingSendTabId;

    /// <summary>Text handed back to the draw thread by a failed translated send.</summary>
    private sealed record PendingRestore(string TabId, string Draft, string Reason);

    // Written by the send continuation, consumed by DrawInput: `drafts`, the
    // history and notifications are all draw-thread state. Swapping one whole
    // record keeps the handover a single atomic reference write.
    private volatile PendingRestore? pendingDraftRestore;

    // Draft to push into the widget's own buffer via InputCallback, for the
    // case where the restore lands while the field still has focus.
    private string? restoreDraftRequested;

    private readonly CommandIndex commandIndex = new();
    private readonly GameChatKeybinds gameKeybinds = new();
    private List<CommandEntry> suggestions = [];
    private int suggestionIndex;
    private string suggestionQuery = string.Empty;
    private bool acceptSuggestionRequested;

    private string? selectTabId;

    // FC membership latch: sticky until logout (see the tab-bar FC check).
    private bool fcSeen;

    private bool enterWasDown;
    private bool slashWasDown;
    private bool pendingSlash;
    private string? pendingInsert;
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
            MinimumSize = new Vector2(250, 150),
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

            changeChannelNameHook = Plugin.GameInterop.HookFromAddress<ChangeChannelNameDelegate>(
                (nint)AgentChatLog.MemberFunctionPointers.ChangeChannelName,
                ChangeChannelNameDetour);

            // Signature scan; a patch can invalidate it. The plugin still
            // works without this hook, vanilla chat just steals input focus.
            try
            {
                chatLogActivateHook = Plugin.GameInterop.HookFromSignature<ChatLogActivateDelegate>(
                    ChatLogActivateSig, ChatLogActivateDetour);
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "ChatLog activate signature no longer matches; vanilla input focus is not suppressed");
            }
        }

        setContextTellTargetHook.Enable();
        changeChannelNameHook.Enable();
        chatLogActivateHook?.Enable();
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

    /// <summary>Mirror vanilla chat visibility rules; cutscene/UI-hidden/loading/battle are configurable.</summary>
    public override bool DrawConditions()
    {
        var config = plugin.Configuration;

        if (!Plugin.ClientState.IsLoggedIn)
            return false;
        if (Plugin.Condition[ConditionFlag.CreatingCharacter])
            return false;
        if (config.HideWhenUiHidden && Plugin.GameGui.GameUiHidden)
            return false;
        if (config.HideDuringCutscenes
            && (Plugin.Condition[ConditionFlag.WatchingCutscene]
                || Plugin.Condition[ConditionFlag.WatchingCutscene78]
                || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]))
        {
            return false;
        }

        if (config.HideInLoadingScreens
            && (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]))
        {
            return false;
        }

        if (config.HideInBattle && Plugin.Condition[ConditionFlag.InCombat])
            return false;

        return true;
    }

    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        setContextTellTargetHook.Dispose();
        changeChannelNameHook.Dispose();
        chatLogActivateHook?.Dispose();
        SetVanillaChatVisible(true);
        gameFont.Dispose();
    }

    public override void PreDraw()
    {
        FFTheme.Configure(plugin.Configuration);

        Flags = plugin.Configuration.LockWindow
            ? BaseFlags | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
            : BaseFlags;

        // Game Default draws its own corner grip. ImGui's native one is claimed
        // during Begin, so it wins the click over anything drawn later — which
        // in that layout is the strip's Close/Lock buttons, sitting in the same
        // bottom-right corner. Dropping it hands the corner back to them.
        if (FFTheme.GameLayout)
            Flags |= ImGuiWindowFlags.NoResize;

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

        // Native tooltip follows hover with a small grace so the gap between
        // two words of the same link doesn't flicker it closed.
        if (nativeTooltipItem != 0 && --nativeTooltipGrace <= 0)
        {
            NativeItemTooltip.Close();
            nativeTooltipItem = 0;
        }
    }

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

        // Polled every tick, including through zone changes and logout, where
        // a bad dereference is an access violation rather than a catchable
        // exception — i.e. the game process, not just the plugin.
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null)
            return false;

        var unitManager = &stage->RaptureAtkUnitManager->AtkUnitManager;
        foreach (var entry in unitManager->FocusedUnitsList.Entries)
        {
            var unit = entry.Value;
            if (unit != null && unit->NameString == "ChatLog")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs before the game processes input for the tick, so consuming a
    /// key here keeps the vanilla chat box from opening. Render-time checks
    /// are too late: the game has already reacted to the key by then.
    /// </summary>
    private unsafe void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        // Keybinds are polled here, not at draw time: framework update runs
        // before the game's own input dispatch, so keys the game would
        // consume and clear (Alt+R reply) are still visible and can be
        // swallowed first. While ImGui captures the keyboard the game key
        // state is suppressed — the draw-time ImGui-source poll takes over.
        if (IsOpen && !ImGui.GetIO().WantTextInput && DrawConditions())
            gameKeybinds.Poll(this, fromImGui: false);

        // Window hidden mid-hover (cutscene, toggle) skips PostDraw, which
        // would leave the game tooltip stranded on screen.
        if (nativeTooltipItem != 0 && (!IsOpen || !DrawConditions()))
        {
            NativeItemTooltip.Close();
            nativeTooltipItem = 0;
        }

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

        // After a send unfocuses the input, the game would see the still-held
        // Enter as a fresh press and open vanilla chat; eat it until released.
        if (suppressEnterUntilReleased)
        {
            if (Plugin.KeyState[VirtualKey.RETURN])
                Plugin.KeyState[VirtualKey.RETURN] = false;
            else
                suppressEnterUntilReleased = false;
        }

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

    /// <summary>
    /// Per-frame bookkeeping every part of the window reads afterwards, kept
    /// out of Draw so what remains there is the layout in order.
    /// </summary>
    private void UpdateFrameState()
    {
        if (!plugin.Configuration.PlacedAtVanillaChat)
            TryPlaceAtVanillaChat();

        // Counterpart of the framework-update poll for when our own input
        // field holds the keyboard (game key state is suppressed then).
        if (ImGui.GetIO().WantTextInput)
            gameKeybinds.Poll(this, fromImGui: true);

        mainWindowPos = ImGui.GetWindowPos();
        mainWindowSize = ImGui.GetWindowSize();

        // PlayerState hands out a cached managed string (no per-frame
        // SeString parse like LocalPlayer.Name); derive only on change.
        var fullName = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CharacterName : string.Empty;
        if (fullName != localFullName)
        {
            localFullName = fullName;
            var firstSpace = localFullName.IndexOf(' ');
            localFirstName = firstSpace > 0 ? localFullName[..firstSpace] : string.Empty;
        }

        // Hashed at window-root scope so tab items can open it from within
        // their own ID scope.
        tabContextPopupId = ImGui.GetID(PlayerContextPopup);
    }

    /// <summary>
    /// Whether FC-only tabs should be shown. A positive membership result
    /// latches until logout so the tab can't flicker away on transient
    /// unreadable states, and a loading (null) player counts as "in one".
    /// </summary>
    private bool ShouldShowFcTabs()
    {
        // Membership comes from the FC info proxy; see ReadInFreeCompany.
        if (!Plugin.ClientState.IsLoggedIn)
            fcSeen = false;
        else if (!fcSeen && ReadInFreeCompany())
            fcSeen = true;

        return fcSeen || Plugin.ObjectTable.LocalPlayer == null;
    }

    /// <summary>Submits one tab header per tab, and the content of the selected one.</summary>
    private void DrawTabItems(TabState[] snapshot)
    {
        imguiIdToTabId.Clear();
        submittedOrder.Clear();
        unreadOffLeft = false;
        unreadOffRight = false;
        var showFcTabs = ShouldShowFcTabs();

        foreach (var tab in snapshot)
        {
            // FC-only tabs are pointless without a free company.
            if (!showFcTabs && IsFcOnlyTab(tab))
            {
                // A selection targeting the hidden tab (Alt+F without an FC)
                // would linger unconsumed and block focus handling.
                if (selectTabId == tab.Id)
                    selectTabId = null;
                continue;
            }

            DrawTabItem(tab);
            submittedOrder.Add(tab.Id);
        }
    }

    private void DrawTabItem(TabState tab)
    {
        // Constant label (badge drawn as an overlay) so tab widths never
        // jump when unread counts appear and disappear.
        var showPresence = tab.IsTell && plugin.Configuration.ShowTellPresence;
        var label = tab.Label(showPresence);

        // Consumed when applied: a switch set mid-loop targeting a tab
        // drawn EARLIER in this frame must survive into the next frame's
        // pass (backward Shift+Tab, wrap-around from the last tab).
        var itemFlags = ImGuiTabItemFlags.None;
        if (selectTabId == tab.Id)
        {
            itemFlags = ImGuiTabItemFlags.SetSelected;
            selectTabId = null;
        }

        if (!tab.IsTell)
        {
            using var fixedItem = ImRaii.TabItem(label, itemFlags);
            imguiIdToTabId[ImGuiP.GetItemID()] = tab.Id;
            NoteOffStripUnread(tab);
            if (fixedItem.Success)
                DrawTab(tab);
            else
                DrawUnreadBadge(tab);
            return;
        }

        var open = true;
        using (var item = ImRaii.TabItem(label, ref open, itemFlags))
        {
            imguiIdToTabId[ImGuiP.GetItemID()] = tab.Id;
            NoteOffStripUnread(tab);
            // The tab header is the last item here, before DrawTab
            // submits the log and input.
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                contextPartner = tab.TellPartner;
                ImGui.OpenPopup(tabContextPopupId);
            }

            if (showPresence)
                DrawPresenceDot(tab);
            // The selected tab reads itself (DrawTab marks it read); a
            // badge there would flash for the one frame between a message
            // arriving and that mark-read, so only unselected tabs badge.
            if (item.Success)
                DrawTab(tab);
            else
                DrawUnreadBadge(tab);
        }

        if (!open)
        {
            tabs.Close(tab);

            // Or reopening this partner later resurrects a stale draft.
            drafts.Remove(tab.Id);
        }
    }

    public override void Draw()
    {
        UpdateFrameState();

        FFTheme.DrawWindowBackground();

        // Game Default is a different layout, not just a palette: no header,
        // input above a hand-drawn tab strip. See MainWindow.Vanilla.cs.
        if (FFTheme.GameLayout)
        {
            DrawGameLayout();
            return;
        }

        DrawHeader();

        // Submitted before the tab bar so the arrows win clicks over tabs
        // beneath them (earliest overlapping ImGui item gets the click). Uses
        // previous-frame strip geometry, incl. the reserved gutters below.
        TabScrollArrowInputs();

        if (focusInput)
            ImGui.SetWindowFocus();

        // When the strip overflows we reserve a gutter at each end for the
        // scroll arrows so they sit in dead space beside the tabs, not over
        // them (a tab dragged to the edge would otherwise slide under an arrow
        // and hide its close button). Whether it overflows is only known from
        // the PREVIOUS frame — the current bar is created by BeginTabBar below —
        // so on the first overflow frame the gutters are absent and the arrows
        // appear one frame later; a harmless one-frame settle.
        //
        // Gutter width is the tab-header height (== arrow size), stable
        // frame-to-frame, so it can be computed before the bar exists.
        var gutter = tabBarOverflowing ? ImGui.GetFrameHeight() : 0f;
        tabArrowSize = gutter;

        // Left gutter: shift the bar's start right. Right gutter: pull in the
        // window work-rect right edge (which BeginTabBar reads to size BarRect).
        // Restored immediately after construction so tab CONTENT (the log/input
        // child, width -1) still uses the full width.
        var window = ImGuiP.GetCurrentWindow();
        var savedWorkRight = window.WorkRect.Max.X;
        if (gutter > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + gutter);
            window.WorkRect.Max.X = savedWorkRight - gutter;
        }

        // FittingPolicyScroll: overflowing tabs scroll horizontally instead of
        // shrinking. The native scroll arrows can't show a disabled state, so
        // they're hidden and we draw our own (see DrawTabScrollArrowVisuals).
        using var tabBar = ImRaii.TabBar(
            "##tabs",
            ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll
            | ImGuiTabBarFlags.NoTabListScrollingButtons);
        window.WorkRect.Max.X = savedWorkRight;
        if (!tabBar.Success)
            return;

        var snapshot = tabs.Snapshot();
        DrawTabItems(snapshot);

        SyncTabOrder();
        UpdateTabScroll();
        DrawTabScrollArrowVisuals();
        DrawPlayerContextMenu();

        // A request naming a tab that no longer exists (closed in the gap)
        // would otherwise linger and block focus handling forever.
        if (selectTabId is { } pending && Array.TrueForAll(snapshot, t => t.Id != pending))
            selectTabId = null;
    }

    /// <summary>
    /// Whether the local character is in a free company. Primary source is
    /// the FC info proxy, fed by the zone-init packet — the reason FC chat
    /// keeps working in instanced duties, where the nameplate CompanyTag
    /// (the fallback) reads empty.
    /// </summary>
    private static unsafe bool ReadInFreeCompany()
    {
        var infoModule = InfoModule.Instance();
        var proxy = infoModule == null
            ? null
            : (InfoProxyFreeCompany*)infoModule->GetInfoProxyById(InfoProxyId.FreeCompany);
        if (proxy != null && proxy->NameString.Length > 0)
            return true;

        return Plugin.ObjectTable.LocalPlayer is { } player
               && player.CompanyTag.TextValue.Length > 0;
    }

    /// <summary>True when every channel of the tab is Free Company chat.</summary>
    private static bool IsFcOnlyTab(TabState tab)
    {
        if (tab.IsTell || tab.CatchAll || tab.Channels is not { Count: > 0 } channels)
            return false;

        // foreach: HashSet's struct enumerator, unlike LINQ All (per frame).
        foreach (var channel in channels)
        {
            if (channel != XivChatType.FreeCompany)
                return false;
        }

        return true;
    }

    private const string PlayerContextPopup = "player-context";
    private uint tabContextPopupId;
    private uint logContextPopupId;
    private string? contextPartner;

    /// <summary>
    /// Context menu for a right-clicked player (name in the log or tell tab
    /// header). Entity-bound actions gray out while the player is not nearby.
    /// Must be called in the same ID scope its popup id was hashed in.
    /// </summary>
    private const string MessageContextPopup = "message-context";
    private uint messageContextPopupId;
    private Message? contextMessage;

    /// <summary>True once a link's own menu has taken this frame's right-click.</summary>
    private bool linkClaimedRightClick;

    /// <summary>
    /// Right-click menu for a whole message row: translate this one line on
    /// demand, whether or not automatic translation is on, and put it back.
    /// </summary>
    private void DrawMessageContextMenu()
    {
        if (contextMessage is not { } message)
            return;

        using var popup = ImRaii.Popup(MessageContextPopup);
        if (!popup.Success)
            return;

        var translation = message.Translation;
        if (translation is { Status: TranslationStatus.Done, Text: not null })
        {
            if (ImGui.Selectable("Show original"))
                TranslationService.ShowOriginal(message);
        }
        else
        {
            var busy = translation is { Status: TranslationStatus.Pending };
            var paused = plugin.Translation.Paused;
            using (ImRaii.Disabled(busy || paused))
            {
                if (ImGui.Selectable(busy ? "Translating…" : "Translate"))
                    plugin.Translation.RequestManual(message);
            }

            // This is the one path that sends text without the settings tab's
            // confirmation in front of it, so it names its destination here.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(paused
                    ? plugin.Translation.LastError ?? "Translation is paused."
                    : $"Sends this line to {ProviderName()}.");
            }
        }

        if (ImGui.Selectable("Copy Text"))
        {
            ImGui.SetClipboardText(message.Text);
            Notify("Copied message text");
        }
    }

    /// <summary>Where a translation request would go, for the menu tooltip.</summary>
    private string ProviderName() => (TranslationProviderKind)plugin.Configuration.TranslationProvider switch
    {
        TranslationProviderKind.DeepL => "DeepL",
        TranslationProviderKind.Anthropic => "Anthropic",
        TranslationProviderKind.OpenAiCompatible => "the configured API endpoint",
        _ => "Google, Bing or Yandex translate",
    };

    private void DrawPlayerContextMenu()
    {
        if (contextPartner is not { } partner)
            return;

        using var popup = ImRaii.Popup(PlayerContextPopup);
        if (!popup.Success)
            return;

        var (name, _) = PlayerActions.Split(partner);

        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.GoldBright))
        {
            ImGui.TextUnformatted(partner);
        }

        ImGui.Separator();

        if (ImGui.Selectable("Send Tell"))
            OpenTellTabFor(partner);

        var nearby = PlayerActions.FindNearby(partner);
        using (ImRaii.Disabled(nearby == null))
        {
            // Tooltip checked per item: after the block, IsItemHovered would
            // only cover the last selectable.
            void NearbyOnlyTooltip()
            {
                if (nearby == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Only available while the player is nearby.");
            }

            if (ImGui.Selectable("Target"))
                PlayerActions.Target(nearby!);
            NearbyOnlyTooltip();
            if (ImGui.Selectable("Examine"))
                PlayerActions.Examine(nearby!);
            NearbyOnlyTooltip();
            if (ImGui.Selectable("Adventurer Plate"))
                PlayerActions.OpenAdventurerPlate(nearby!);
            NearbyOnlyTooltip();
        }

        if (ImGui.Selectable("Invite to Party") && !PlayerActions.InviteToParty(partner))
            Notify($"Could not invite {name}.");

        if (ImGui.Selectable("Copy Name"))
        {
            ImGui.SetClipboardText(name);
            Notify($"Copied \"{name}\"");
        }
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

    // An unread tab scrolled out of the strip takes its badge with it, so the
    // arrow pointing at it carries a dot instead. Recomputed every frame while
    // the headers are submitted; like the gutters, the strip bounds they are
    // tested against are last frame's, which costs at most a frame of lag.
    private bool unreadOffLeft;
    private bool unreadOffRight;

    /// <summary>Badge bubble and the off-strip dot that stands in for it.</summary>
    private static readonly Vector4 UnreadColor = new(0.80f, 0.20f, 0.20f, 1f);

    // Set from last frame's tabScrollMax; drives this frame's gutter reservation
    // (decided before BeginTabBar creates the current bar — see Draw).
    private bool tabBarOverflowing;

    // Reserved gutter width for this frame's arrows. Equals the tab-header
    // height, chosen before the bar exists so arrow inputs/visuals match the
    // inset applied to the bar. Zero when the strip fits (no gutters, no arrows).
    private float tabArrowSize;

    private float TabArrowSize => tabArrowSize;

    private bool LeftArrowEnabled => tabScrollCurrent > 0.5f;
    private bool RightArrowEnabled => tabScrollCurrent < tabScrollMax - 0.5f;

    // Arrows live in the reserved gutters, OUTSIDE the inset strip: the left one
    // just left of tabStripMin, the right one just right of tabStripMax. So a
    // tab dragged to either edge stays fully visible between them.
    private Vector2 LeftArrowPos => new(tabStripMin.X - TabArrowSize, tabStripMin.Y);
    private Vector2 RightArrowPos => new(tabStripMax.X, tabStripMin.Y);

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

        // Latched for next frame's gutter decision (the bar for the NEXT frame
        // doesn't exist yet when we must decide whether to inset it). Once the
        // inset strip overflows, keeping the gutters keeps it overflowing, so
        // the state latches on rather than oscillating at the fit boundary.
        tabBarOverflowing = tabScrollMax > 0f;

        // Wheel over the strip scrolls it. IsWindowHovered() (used before) reads
        // false while the cursor is over a TabItem header — a hovered/active item
        // suppresses the plain window-hover test, so the wheel never fired there.
        // A pure geometric hit-test against the strip rect avoids that entirely.
        // NoScrollWithMouse (window flag) only stops ImGui from APPLYING wheel to
        // the window's own scroll; it does not zero io.MouseWheel, so we still see it.
        var wheel = ImGui.GetIO().MouseWheel;
        if (tabScrollMax > 0f && wheel != 0f
            && ImGui.IsMouseHoveringRect(tabStripMin, tabStripMax))
        {
            pendingTabScroll -= wheel * 80f;
        }

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

        DrawTabArrow(LeftArrowPos, left: true, LeftArrowEnabled, leftArrowHovered, unreadOffLeft);
        DrawTabArrow(RightArrowPos, left: false, RightArrowEnabled, rightArrowHovered, unreadOffRight);
    }

    private void DrawTabArrow(Vector2 pos, bool left, bool enabled, bool hovered, bool unread)
    {
        var size = TabArrowSize;
        var drawList = ImGui.GetWindowDrawList();
        // No backing rect anymore: the arrows sit in reserved gutters (dead
        // space beside the tabs), so there are no tabs scrolling beneath them.

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

        if (!unread)
            return;

        // Sits in the gutter's outer top corner, away from the tabs, and
        // pulses with the badge it stands in for so the two read as the same
        // signal. No count: it can speak for several tabs at once.
        var dotRadius = MathF.Max(2f, size * 0.15f);
        var dotCenter = new Vector2(
            left ? pos.X + dotRadius + 1f : pos.X + size - dotRadius - 1f,
            pos.Y + dotRadius + 1f);

        var pulse = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 3.5f);
        drawList.AddCircleFilled(
            dotCenter, dotRadius, ImGui.GetColorU32(UnreadColor with { W = 0.55f + 0.45f * pulse }));
    }

    private readonly Dictionary<uint, string> imguiIdToTabId = [];
    private readonly List<string> orderScratch = [];

    /// <summary>Tabs submitted this frame, in our order — the order they should display in.</summary>
    private readonly List<string> submittedOrder = [];
    private readonly HashSet<string> submittedLastFrame = [];

    /// <summary>ImGui id and bar slot of each submitted tab, in display order.</summary>
    private readonly List<(uint ImGuiId, int Slot)> barOrder = [];

    // One displaced tab is moved back per frame (ImGui holds a single reorder
    // request), and a move only lands at the next BeginTabBar; the budget is
    // an escape hatch so a restore that can't converge can't wedge the sync.
    private const int RestoreOrderFrames = 30;
    private int restoreOrderFrames;

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

        // Mid-drag, ImGui has a reorder queued it hasn't applied yet;
        // resubmitting in a new order now trips its internal assertions.
        if (bar.ReorderRequestTabId != 0)
            return;

        orderScratch.Clear();
        barOrder.Clear();
        for (var i = 0; i < bar.Tabs.Size; i++)
        {
            var imguiId = bar.Tabs[i].ID;
            if (!imguiIdToTabId.TryGetValue(imguiId, out var tabId))
                continue;

            orderScratch.Add(tabId);
            barOrder.Add((imguiId, i));
        }

        if (RestoreTabOrder(bar))
            return;

        tabs.SetOrder(orderScratch);
    }

    /// <summary>
    /// A tab that stopped being submitted and came back — the FC tab whenever
    /// the info proxy reads empty for a few frames, e.g. right after a login —
    /// is APPENDED to the tab bar: for a reorderable bar ImGui keeps its own
    /// order and ignores submission order. Nothing asked for that position, so
    /// pushing it into the config (which the plain sync would do next) would
    /// move the tab to the end of the strip for good. Reorder the bar back to
    /// our order instead, and persist nothing until it has taken.
    /// </summary>
    /// <returns>True while a restore is in progress.</returns>
    private bool RestoreTabOrder(ImGuiTabBarPtr bar)
    {
        var appeared = false;
        foreach (var tabId in submittedOrder)
        {
            if (!submittedLastFrame.Contains(tabId))
            {
                appeared = true;
                break;
            }
        }

        submittedLastFrame.Clear();
        submittedLastFrame.UnionWith(submittedOrder);

        // A tab appearing for the first time (a tell tab opening) lands at the
        // end of both orders, so it trips this and then finds nothing to fix.
        if (appeared)
            restoreOrderFrames = RestoreOrderFrames;

        if (restoreOrderFrames <= 0)
            return false;

        restoreOrderFrames--;

        for (var i = 0; i < submittedOrder.Count && i < orderScratch.Count; i++)
        {
            if (orderScratch[i] == submittedOrder[i])
                continue;

            var from = orderScratch.IndexOf(submittedOrder[i]);
            if (from < 0)
                break;

            var tab = ImGuiP.TabBarFindTabByID(bar, barOrder[from].ImGuiId);
            if (tab.IsNull)
                break;

            // Offsets count slots in the bar's own list, which can still hold
            // a tab that was dropped from this frame's submission.
            ImGuiP.TabBarQueueReorder(bar, tab, barOrder[i].Slot - barOrder[from].Slot);
            return true;
        }

        restoreOrderFrames = 0;
        return false;
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

    /// <summary>
    /// Draws the partner's online status as a dot at the left edge of the tab
    /// header (the last ImGui item): green online, red AFK, gray offline,
    /// blue unknown (not a friend, not in party, not nearby — no data).
    /// </summary>
    private void DrawPresenceDot(TabState tab)
    {
        var color = PresenceColor(plugin.Presence.StatusFor(tab.TellPartner!));

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = ImGui.GetFontSize() * 0.18f;
        var center = new Vector2(min.X + radius + 5f, (min.Y + max.Y) / 2f + 1f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.GetColorU32(color), 12);
    }

    /// <summary>Dot color for a tell partner's online status; shared by both tab strips.</summary>
    private static Vector4 PresenceColor(PresenceStatus status) => status switch
    {
        PresenceStatus.Online => new Vector4(0.35f, 0.85f, 0.40f, 1f),
        PresenceStatus.Afk => new Vector4(0.90f, 0.30f, 0.25f, 1f),
        PresenceStatus.Offline => new Vector4(0.55f, 0.55f, 0.55f, 0.90f),

        // Not a friend, not in party, not nearby — no data.
        _ => new Vector4(0.35f, 0.55f, 0.95f, 0.90f),
    };

    /// <summary>
    /// Flags an unread tab whose badge is scrolled out of the strip, so the
    /// arrow on that side can stand in for it. Called with the tab header as
    /// the last submitted item; the badge sits at the header's top-right, so
    /// that corner — not the header — decides whether it is really visible.
    /// </summary>
    private void NoteOffStripUnread(TabState tab)
    {
        // Nothing to point at, or no strip geometry yet (first frame).
        if (tab.Unread <= 0 || tabStripMax.X <= tabStripMin.X)
            return;

        var badgeX = ImGui.GetItemRectMax().X - UnreadBadgeRadius() - 2f;
        if (badgeX <= tabStripMin.X)
            unreadOffLeft = true;
        else if (badgeX >= tabStripMax.X)
            unreadOffRight = true;
    }

    private static float UnreadBadgeRadius() => ImGui.GetFontSize() * 0.42f;

    /// <summary>
    /// Draws the unread markers over the tab header (the last ImGui item):
    /// a pulsing outline plus a count bubble.
    /// </summary>
    private static void DrawUnreadBadge(TabState tab)
    {
        if (tab.Unread <= 0)
            return;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        var pulse = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 3.5f);
        var glow = FFTheme.GoldBright with { W = 0.25f + 0.55f * pulse };
        drawList.AddRect(
            min + new Vector2(1f, 1f), max - new Vector2(1f, 1f),
            ImGui.GetColorU32(glow), 4f, ImDrawFlags.None, 2f);

        var radius = UnreadBadgeRadius();
        var center = new Vector2(max.X - radius - 2, min.Y + radius + 1);
        var text = tab.Unread > 9 ? "9+" : tab.Unread.ToString();

        drawList.AddCircleFilled(center, radius + 2, ImGui.GetColorU32(UnreadColor));

        var scale = 0.75f;
        var size = ImGui.CalcTextSize(text) * scale;
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, center - size / 2, 0xFFFFFFFF, text);
    }

    private void DrawTab(TabState tab)
    {
        tabs.MarkRead(tab);

        // An armed <item> placeholder shows a preview line above the input;
        // the log child must leave room for it.
        drafts.TryGetValue(tab.Id, out var pendingDraft);
        var linkPreview = pendingDraft?.Contains("<item>", StringComparison.Ordinal) == true
            ? LinkedItemName()
            : null;
        var reserve = ImGui.GetFrameHeightWithSpacing()
                      + (linkPreview != null ? ImGui.GetTextLineHeightWithSpacing() : 0);

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2)))
        using (var child = ImRaii.Child("##log", new Vector2(-1, -reserve), false))
        {
            if (child.Success)
                DrawLog(tab);
        }

        if (linkPreview != null)
            ImGui.TextColored(ChatColors.Link, $"{SeIconChar.LinkMarker.ToIconChar()} {linkPreview}");

        DrawInput(tab);
    }

    /// <summary>Name of the item staged behind an armed &lt;item&gt; placeholder, null if none.</summary>
    private static unsafe string? LinkedItemName()
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return null;

        var itemId = agent->LinkedItem.ItemId;
        if (itemId == 0)
            return null;

        // Linked ids encode the variant: +500k collectible, +1M HQ, 2M+ event item.
        var hq = itemId is >= 1_000_000 and < 2_000_000;
        var baseId = itemId switch
        {
            < 500_000 => itemId,
            < 1_000_000 => itemId - 500_000,
            < 2_000_000 => itemId - 1_000_000,
            _ => itemId,
        };

        var name = GameData.ItemName(baseId, eventItem: itemId >= 2_000_000);
        if (string.IsNullOrEmpty(name))
            return null;

        return hq ? $"{name} {SeIconChar.HighQuality.ToIconChar()}" : name;
    }

    private void DrawLog(TabState tab)
    {
        // Hashed at the log child's root scope; DrawToken opens it from
        // within message rows, DrawPlayerContextMenu below begins it here.
        logContextPopupId = ImGui.GetID(PlayerContextPopup);
        itemContextPopupId = ImGui.GetID(ItemContextPopup);
        messageContextPopupId = ImGui.GetID(MessageContextPopup);

        // Rearmed by whichever row is hovered this frame; cleared here so it
        // can't linger once the cursor leaves.
        pendingTranslationTooltip = null;

        // A link under the cursor owns the right-click (its menu is the more
        // specific one); the row menu only takes what no link claimed.
        linkClaimedRightClick = false;

        var messages = tabs.MessagesSnapshot(tab);
        // First draw of a tab (e.g. history just hydrated) starts pinned.
        var firstDraw = tab.RenderedRevision == -1;

        // Only the selected tab draws, so the drawn tab changing IS the user
        // switching to this one. Switching always lands on the newest line:
        // a tab left scrolled up would otherwise reopen mid-backlog, showing
        // stale lines while its badge says there is something new.
        var justSelected = lastDrawnTabId != tab.Id;
        lastDrawnTabId = tab.Id;

        // Sending is a commitment to the live conversation: however far up the
        // backlog the log was scrolled, it lands on the newest line. One shot
        // is enough — being at the bottom re-pins it, so the echo of the sent
        // line and everything after it follow on their own.
        var justSent = scrollLogToBottom;
        scrollLogToBottom = false;

        var pinnedToBottom = firstDraw || ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
        var newMessages = firstDraw || tab.Revision != tab.RenderedRevision;
        tab.RenderedRevision = tab.Revision;

        if (messages.Length == 0)
        {
            using var dim = ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp);
            const string empty = "No messages yet — chat will appear here as it happens.";
            if (FFTheme.GameLayout)
                VanillaChrome.ShadowAtCursor(empty);
            ImGui.TextWrapped(empty);
            return;
        }

        var collapse = plugin.Configuration.CollapseDuplicates;
        var first = Math.Max(0, messages.Length - MaxRenderedMessages);
        for (var i = first; i < messages.Length; i++)
        {
            if (i > first && messages[i - 1].Timestamp.Date != messages[i].Timestamp.Date)
                DrawDateSeparator(messages[i].Timestamp);

            // Consecutive identical lines render once with a ×N counter.
            var repeats = 1;
            while (collapse
                   && i + 1 < messages.Length
                   && messages[i + 1].Type == messages[i].Type
                   && messages[i + 1].Sender == messages[i].Sender
                   && messages[i + 1].Text == messages[i].Text)
            {
                i++;
                repeats++;
            }

            var lineStart = ImGui.GetCursorScreenPos();
            var lineWidth = ImGui.GetContentRegionAvail().X;
            DrawMessage(messages[i], repeats);

            var rowBottom = ImGui.GetItemRectMax().Y;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)
                && !linkClaimedRightClick
                && ImGui.IsMouseHoveringRect(lineStart, new Vector2(lineStart.X + lineWidth, rowBottom)))
            {
                contextMessage = messages[i];
                ImGui.OpenPopup(messageContextPopupId);
            }

            if (IsMention(messages[i]))
            {
                var bottom = rowBottom;
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(
                    lineStart, new Vector2(lineStart.X + lineWidth, bottom),
                    ImGui.GetColorU32(FFTheme.Gold with { W = 0.09f }));
                drawList.AddRectFilled(
                    lineStart - new Vector2(4f, 0f), new Vector2(lineStart.X - 2f, bottom),
                    ImGui.GetColorU32(FFTheme.GoldBright with { W = 0.85f }));
            }
        }

        if (pendingTranslationTooltip is { } tooltip)
            DrawTranslationTooltip(tooltip.Message, tooltip.Translation);

        DrawPlayerContextMenu();
        DrawItemContextMenu();
        DrawMessageContextMenu();

        if (justSelected || justSent || (pinnedToBottom && newMessages))
            ImGui.SetScrollHereY(1f);
    }

    /// <summary>Tab whose log drew last frame; a change means the user switched tabs.</summary>
    private string? lastDrawnTabId;

    private const string ItemContextPopup = "item-context";
    private uint itemContextPopupId;
    private SegmentLink.Item? contextItem;

    private uint nativeTooltipItem;
    private int nativeTooltipGrace;
    private Vector2 mainWindowPos;
    private Vector2 mainWindowSize;

    /// <summary>Vanilla-style actions for a clicked item link.</summary>
    private void DrawItemContextMenu()
    {
        if (contextItem is not { } item)
            return;

        using var popup = ImRaii.Popup(ItemContextPopup);
        if (!popup.Success)
            return;

        var name = item.Name ?? $"Item #{item.ItemId}";

        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.GoldBright))
        {
            ImGui.TextUnformatted(item.Hq ? $"{name} {SeIconChar.HighQuality.ToIconChar()}" : name);
        }

        ImGui.Separator();

        // Event/key items (2M+) only support linking and copying.
        var normalItem = item.ItemId < 500_000;
        var equippable = normalItem
                         && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                             .TryGetRow(item.ItemId, out var row)
                         && row.EquipSlotCategory.RowId != 0;

        if (equippable)
        {
            if (ImGui.Selectable("Try On"))
                ItemActions.TryOn(item.ItemId);
            if (ImGui.Selectable("Item Comparison"))
                ItemActions.Compare(item.ItemId);
        }

        if (normalItem)
        {
            if (ImGui.Selectable("Search for Item"))
                ItemActions.SearchForItem(item.ItemId);
            if (ImGui.Selectable("Search Recipes Using This Material"))
                ItemActions.SearchRecipes(item.ItemId);
        }

        if (ImGui.Selectable("Link"))
            ItemActions.Link(item.ItemId);

        if (ImGui.Selectable("Copy Item Name"))
        {
            ImGui.SetClipboardText(name);
            Notify($"Copied \"{name}\"");
        }
    }

    /// <summary>Dim centered "— Tuesday, July 8 —" rule between messages of different days.</summary>
    private static void DrawDateSeparator(DateTime date)
    {
        var label = date.ToString("dddd, MMMM d");
        var width = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(label);

        ImGui.Dummy(new Vector2(1f, 3f));
        var pos = ImGui.GetCursorScreenPos();
        var textX = pos.X + (width - textSize.X) / 2f;
        var y = pos.Y + textSize.Y / 2f;
        var drawList = ImGui.GetWindowDrawList();
        var lineColor = ImGui.GetColorU32(FFTheme.TextDim with { W = 0.35f });
        const float pad = 8f;

        drawList.AddLine(new Vector2(pos.X, y), new Vector2(textX - pad, y), lineColor);
        drawList.AddLine(new Vector2(textX + textSize.X + pad, y), new Vector2(pos.X + width, y), lineColor);

        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - textSize.X) / 2f);
            if (FFTheme.GameLayout)
                VanillaChrome.ShadowAtCursor(label);
            ImGui.TextUnformatted(label);
        }

        ImGui.Dummy(new Vector2(1f, 3f));
    }

    private string localFullName = string.Empty;
    private string localFirstName = string.Empty;

    private bool IsMention(Message message)
    {
        if (!plugin.Configuration.HighlightMentions || localFullName.Length == 0)
            return false;

        // Own messages quote your name constantly (emotes, tells); skip them.
        if (message.Sender.StartsWith(localFullName, StringComparison.Ordinal))
            return false;

        return ContainsWord(message.Text, localFullName)
               || (localFirstName.Length > 0 && ContainsWord(message.Text, localFirstName));
    }

    private static bool ContainsWord(string text, string word)
    {
        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var boundaryBefore = index == 0 || !char.IsLetter(text[index - 1]);
            var end = index + word.Length;
            var boundaryAfter = end >= text.Length || !char.IsLetter(text[end]);
            if (boundaryBefore && boundaryAfter)
                return true;
            index = end;
        }

        return false;
    }
}
