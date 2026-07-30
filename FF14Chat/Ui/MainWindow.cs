using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
using FF14Chat.Services.Translation;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Chat.Ui;

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
    private bool focusInput;
    private bool suppressEnterUntilReleased;

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

    // The ChatLog addon's event handler; event 0x31 with value 0x05/0x0C is
    // the game asking the vanilla chat input to activate and take focus
    // (chat keybind, reply, social-window Send Tell). Signature as used by
    // ChatTwo for the same purpose.
    private const string ChatLogActivateSig =
        "40 53 57 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 4D 8B F8";

    private unsafe delegate byte ChatLogActivateDelegate(nint addon, ushort eventId, AtkValue* value);

    private readonly Hook<ChatLogActivateDelegate>? chatLogActivateHook;

    /// <summary>
    /// While we're the active chat, chat-activation requests focus our input
    /// instead of the vanilla one. The tell target (if any) was already set:
    /// ChangeChannelName ran before this event fired.
    /// </summary>
    private unsafe byte ChatLogActivateDetour(nint addon, ushort eventId, AtkValue* value)
    {
        try
        {
            if (eventId == 0x31 && value != null && value->UInt is 0x05 or 0x0C)
            {
                // The third value can carry text the game wants pre-filled
                // into the input — the social window's Send Tell passes the
                // whole "/tell Name@World " command this way instead of
                // switching the chat mode.
                var insertValue = value + 2;
                var insert = ((int)insertValue->Type & 0xF) == (int)AtkValueType.String
                             && insertValue->String.HasValue
                    ? insertValue->String.ToString()
                    : string.Empty;

                var consume = IsOpen && plugin.Configuration.HideVanillaChat && Plugin.ClientState.IsLoggedIn;
                Plugin.Log.Debug(
                    "ChatLog activate: value={Value:X} insert='{Insert}' consumed={Consumed}",
                    value->UInt, insert, consume);
                if (consume)
                {
                    if (insert.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase))
                    {
                        var partner = insert["/tell ".Length..].Trim();
                        Plugin.Framework.RunOnTick(() => OpenTellTabFor(partner));
                    }
                    else if (insert.Length > 0)
                    {
                        pendingInsert = insert;
                    }

                    // 0x0C is the tell-target activation; only that flavor
                    // may trust the staged Temp* fields (they linger after
                    // we swallow the commit, so a plain activation must not
                    // read them).
                    ScheduleTellTabSync(includeStaged: value->UInt == 0x0C);
                    focusInput = true;
                    return 1;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "ChatLog activate detour failed");
        }

        return chatLogActivateHook!.Original(addon, eventId, value);
    }

    /// <summary>Opens/selects the tell tab for "Name@World" (world appended from the local player if missing).</summary>
    private void OpenTellTabFor(string partner)
    {
        if (partner.Length == 0)
            return;

        if (!partner.Contains('@')
            && Plugin.ObjectTable.LocalPlayer is { } local
            && local.HomeWorld.ValueNullable?.Name.ExtractText() is { Length: > 0 } homeWorld)
        {
            partner = $"{partner}@{homeWorld}";
        }

        Plugin.Log.Debug("Opening tell tab for '{Partner}'", partner);
        var tellTab = tabs.OpenTellTab(partner);
        selectTabId = tellTab.Id;
        focusInput = true;
    }

    private unsafe delegate nint ChangeChannelNameDelegate(AgentChatLog* agent);

    private readonly Hook<ChangeChannelNameDelegate> changeChannelNameHook;

    /// <summary>
    /// Runs whenever the game's input channel label changes (this is the
    /// path the social window / friend list uses, which never goes through
    /// SetContextTellTarget). The agent's tell fields are not final yet at
    /// this point mid-flow — the target is written after the label updates —
    /// so the actual read happens one tick later.
    /// </summary>
    private unsafe nint ChangeChannelNameDetour(AgentChatLog* agent)
    {
        var result = changeChannelNameHook.Original(agent);

        // Same rule as the other detours: nothing may throw into game code.
        try
        {
            ScheduleTellTabSync(includeStaged: false);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "ChangeChannelName sync failed");
        }

        return result;
    }

    private void ScheduleTellTabSync(bool includeStaged)
    {
        Plugin.Framework.RunOnTick(() => SyncTellTabFromAgent(includeStaged));
    }

    /// <summary>
    /// If the game's input channel is a tell, mirror its target into a tell
    /// tab. Runs a tick after a channel change or chat activation, when the
    /// agent's target fields are complete.
    /// </summary>
    private unsafe void SyncTellTabFromAgent(bool includeStaged)
    {
        try
        {
            if (!IsOpen || !plugin.Configuration.HideVanillaChat)
                return;

            var shell = RaptureShellModule.Instance();
            var agent = AgentChatLog.Instance();
            if (shell == null || agent == null)
                return;

            // The social window stages a pending tell in the Temp* fields and
            // fires the chat-activation event; the vanilla handler we swallow
            // is what would commit them to the active fields. Prefer staged.
            var chatType = includeStaged ? shell->TempChatType : 0;
            var name = includeStaged ? shell->TempTellName.ToString() : string.Empty;
            var world = includeStaged ? shell->TempTellWorld.ToString() : string.Empty;
            var worldId = includeStaged ? shell->TempTellWorldId : (ushort)0;

            Plugin.Log.Debug(
                "Tell sync: staged={Staged} temp={TempChatType}:'{TempName}'@'{TempWorld}'/{TempWorldId} committed={ChatType}:'{Name}'@'{World}' agent={Channel}:'{AgentName}'@{AgentWorldId}",
                includeStaged,
                shell->TempChatType, shell->TempTellName.ToString(), shell->TempTellWorld.ToString(), shell->TempTellWorldId,
                shell->ChatType, shell->TellName.ToString(), shell->TellWorld.ToString(),
                (int)agent->CurrentChannel, agent->TellPlayerName.ToString(), agent->TellWorldId);

            // RaptureShellModule chat types 17/18 are the two tell modes.
            if (chatType is not (17 or 18) || name.Length == 0)
            {
                chatType = shell->ChatType;
                name = shell->TellName.ToString();
                world = shell->TellWorld.ToString();
                worldId = shell->TellWorldId;
            }

            if (chatType is not (17 or 18) || name.Length == 0)
                return;

            if (world.Length == 0)
                world = GameData.WorldName(worldId);

            var partner = world.Length > 0 ? $"{name}@{world}" : name;
            var tellTab = tabs.OpenTellTab(partner);
            selectTabId = tellTab.Id;
            focusInput = true;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Tell tab sync failed");
        }
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
            Plugin.Log.Debug(
                "SetContextTellTarget: name='{Name}' world='{World}' worldId={WorldId} reason={Reason} open={Open} hideVanilla={Hide}",
                playerName != null ? playerName->ToString() : "<null>",
                worldName != null ? worldName->ToString() : "<null>",
                worldId, reason, IsOpen, plugin.Configuration.HideVanillaChat);

            if (IsOpen && plugin.Configuration.HideVanillaChat && playerName != null)
            {
                var name = playerName->ToString();
                var world = worldName != null ? worldName->ToString() : string.Empty;
                if (world.Length == 0)
                    world = GameData.WorldName(worldId);

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

        var unitManager = &AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager;
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

    public override void Draw()
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

        imguiIdToTabId.Clear();
        var snapshot = tabs.Snapshot();

        var inFreeCompany = UpdateFreeCompanyLatch();

        foreach (var tab in snapshot)
        {
            if (!inFreeCompany && IsFcOnlyTab(tab))
            {
                // A selection targeting the hidden tab (Alt+F without an FC)
                // would linger unconsumed and block focus handling.
                if (selectTabId == tab.Id)
                    selectTabId = null;
                continue;
            }

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

            if (tab.IsTell)
            {
                var open = true;
                using (var item = ImRaii.TabItem(label, ref open, itemFlags))
                {
                    imguiIdToTabId[ImGuiP.GetItemID()] = tab.Id;
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
            else
            {
                using var item = ImRaii.TabItem(label, itemFlags);
                imguiIdToTabId[ImGuiP.GetItemID()] = tab.Id;
                if (item.Success)
                    DrawTab(tab);
                else
                    DrawUnreadBadge(tab);
            }
        }

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

    /// <summary>
    /// Whether FC-only tabs should be shown; they are pointless without a free
    /// company. Membership comes from the FC info proxy (see
    /// <see cref="ReadInFreeCompany"/>); a positive result latches until logout
    /// so the tab can't flicker away on transient unreadable states, and a
    /// loading (null) player counts as "in one".
    /// </summary>
    private bool UpdateFreeCompanyLatch()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            fcSeen = false;
        else if (!fcSeen && ReadInFreeCompany())
            fcSeen = true;

        return fcSeen || Plugin.ObjectTable.LocalPlayer == null;
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

        DrawTabArrow(LeftArrowPos, left: true, LeftArrowEnabled, leftArrowHovered);
        DrawTabArrow(RightArrowPos, left: false, RightArrowEnabled, rightArrowHovered);
    }

    private void DrawTabArrow(Vector2 pos, bool left, bool enabled, bool hovered)
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

        // Mid-drag, ImGui has a reorder queued it hasn't applied yet;
        // resubmitting in a new order now trips its internal assertions.
        if (bar.ReorderRequestTabId != 0)
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

        if (pinnedToBottom && newMessages)
            ImGui.SetScrollHereY(1f);
    }

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

    private bool inputActiveLastFrame;
    private TabState? inputTab;

    // Say -> Party -> Alliance -> FC, the useful everyday rotation.
    private static readonly int[] ChannelCycle = [1, 2, 3, 6];

    /// <summary>Switches the game's active input channel to the next/previous in the cycle.</summary>
    private unsafe void CycleGameChannel(int direction)
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return;

        var index = Array.IndexOf(ChannelCycle, (int)agent->CurrentChannel);
        var next = ChannelCycle[(index + direction + ChannelCycle.Length) % ChannelCycle.Length];
        GameKeybindChannel(next, 0);
    }

    internal void GameKeybindFocus() => focusInput = true;

    internal void GameKeybindSlash()
    {
        pendingSlash = true;
        focusInput = true;
    }

    /// <summary>Reply: open the tell tab of the most recent tell in either direction.</summary>
    internal void GameKeybindReply()
    {
        var messages = plugin.MessageStore.Snapshot();
        for (var i = messages.Length - 1; i >= 0; i--)
        {
            if (messages[i].TellPartner is { Length: > 0 } partner)
            {
                OpenTellTabFor(partner);
                return;
            }
        }
    }

    internal unsafe void GameKeybindChannel(int channel, uint linkshell)
    {
        var shell = RaptureShellModule.Instance();
        if (shell == null)
            return;

        var empty = Utf8String.FromString(string.Empty);
        try
        {
            shell->ChangeChatChannel(channel, linkshell, empty, true);
        }
        finally
        {
            empty->Dtor(true);
        }

        // The channel's dedicated tab (Party, FC, …) is always selected when
        // one exists. Channels without one (yell, linkshells, …) fall back
        // to the first tab following the game's active channel (General),
        // but only when the current tab can't send there.
        var commands = ChannelSendCommands(channel);
        var snapshot = tabs.Snapshot();
        var target = Array.Find(snapshot, t => !t.IsTell && MatchesCommand(t.SendCommand, commands));
        if (target == null && inputTab is not { IsTell: false, SendCommand: null })
            target = Array.Find(snapshot, t => !t.IsTell && t.SendCommand == null);

        if (target != null)
            selectTabId = target.Id;

        focusInput = true;

        static bool MatchesCommand(string? sendCommand, string[]? commands)
        {
            if (sendCommand == null || commands == null)
                return false;

            foreach (var command in commands)
            {
                if (sendCommand.Equals(command, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    /// <summary>Send commands equivalent to a RaptureShell channel number, null if none.</summary>
    private static string[]? ChannelSendCommands(int channel) => channel switch
    {
        1 => ["/s", "/say"],
        2 => ["/p", "/party"],
        3 => ["/a", "/alliance"],
        4 => ["/y", "/yell"],
        5 => ["/sh", "/shout"],
        6 => ["/fc", "/freecompany"],
        8 => ["/n", "/novice"],
        _ => null,
    };

    private void SwitchToNextTab(TabState current, int direction)
    {
        var all = tabs.Snapshot();
        if (all.Length < 2)
            return;

        var index = Array.FindIndex(all, t => t.Id == current.Id);
        selectTabId = all[(index + direction + all.Length) % all.Length].Id;
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

        // A failed translated send hands its text back here instead of writing
        // `drafts` from its continuation thread.
        if (pendingDraftRestore is { } restore)
        {
            pendingDraftRestore = null;
            drafts[restore.TabId] = restore.Draft;

            // The widget owns its buffer while focused, so an external draft
            // write is dropped there — route it through the callback like the
            // other programmatic edits. Focus is deliberately not stolen back:
            // the send already handed control to the game and this lands at an
            // arbitrary moment, possibly mid-fight.
            if (restore.TabId == tab.Id && inputActiveLastFrame)
                restoreDraftRequested = restore.Draft;

            Notify(restore.Reason);
        }

        drafts.TryGetValue(tab.Id, out var draft);
        draft ??= string.Empty;

        // Empty-input Tab cycling. Handled at draw level rather than in the
        // completion callback: ImGui doesn't deliver the completion event for
        // an empty buffer (Tab falls through to keyboard nav instead).
        if (inputActiveLastFrame && draft.Length == 0 && ImGui.IsKeyPressed(ImGuiKey.Tab, false))
        {
            var direction = ImGui.GetIO().KeyShift ? -1 : 1;
            if (tab.IsTell || tab.SendCommand is { Length: > 0 })
                SwitchToNextTab(tab, direction);
            else
                CycleGameChannel(direction);

            // Nav may have stolen focus for this frame; take it back.
            focusInput = true;
        }

        // While the field is focused, tint its border with the channel color
        // the message will be sent in, as a destination indicator.
        var destination = SendDestination(tab);
        using var border = ImRaii.PushColor(
            ImGuiCol.Border, destination?.Color ?? default, inputActiveLastFrame && destination.HasValue);

        // A pending tab switch (selectTabId) takes effect next frame; the
        // outgoing tab must not consume the focus request meant for the
        // incoming tab's input.
        if (focusInput && (selectTabId == null || selectTabId == tab.Id))
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

            // Text the game asked to pre-fill into the chat input (e.g. an
            // emote command from a UI button, or <item> from the inventory
            // Link action). Match vanilla: a command replaces the draft, a
            // placeholder appends to it (once).
            if (pendingInsert is { } insert)
            {
                pendingInsert = null;
                if (insert.StartsWith('/'))
                    draft = insert;
                else if (!draft.Contains(insert, StringComparison.Ordinal))
                    draft += insert;
            }
        }

        // While a translated send is in flight the placeholder doubles as its
        // progress indicator: the field stays fully usable, and it is empty
        // right after a submit — exactly when a hint is visible.
        var translating = pendingSend is { IsCompleted: false } && pendingSendTabId == tab.Id;

        // Game Default puts the destination on the prompt line above the bar
        // (as the game does), so the field itself stays empty there.
        var hint = translating
            ? "Translating…"
            : FFTheme.GameLayout
                ? string.Empty
                : tab.IsTell
                    ? $"Message {tab.Title}…"
                    : destination is { Label.Length: > 0 } dest
                        ? $"{dest.Label}…"
                        : "Chat or /command…";
        var inputPos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(-1);

        // Game Default draws the field's frame itself (square where it meets
        // the chat-mode button, one rounded cap on the right), so the widget
        // gets a transparent frame over it. The rect is known before the widget
        // is submitted: SetNextItemWidth(-1) runs it to the window edge.
        using var vanillaFrame = PushGameInputFrame(inputPos, destination?.Color);

        // InputText can't style a substring. With a link placeholder in the
        // draft, the widget draws its text transparent and the visible text
        // (placeholders in link blue, own caret) is repainted on top by
        // DrawInputTextOverlay.
        var placeholders = FindLinkPlaceholders(draft);
        var hasLinkPlaceholder = placeholders.Count > 0;
        var draftBeforeInput = draft;

        bool submitted;
        using (ImRaii.PushColor(ImGuiCol.Text, Vector4.Zero, hasLinkPlaceholder))
        {
            submitted = ImGui.InputTextWithHint(
                $"##input{tab.Id}", hint, ref draft, ChatSender.MaxBytes,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory
                | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion
                | ImGuiInputTextFlags.CallbackCharFilter,
                InputCallback);
        }
        var inputActive = ImGui.IsItemActive();

        // Dropped if the callback never ran (the field lost focus in the same
        // frame the restore landed): the plain `drafts` write already covers
        // that case, and a stale request would clobber the next thing typed.
        restoreDraftRequested = null;

        if (hasLinkPlaceholder)
        {
            // The widget may have edited the draft this frame; re-scan only then.
            if (!ReferenceEquals(draft, draftBeforeInput))
                placeholders = FindLinkPlaceholders(draft);
            DrawInputTextOverlay(draft, placeholders, inputActive);
        }
        inputActiveLastFrame = inputActive;
        drafts[tab.Id] = draft;

        UpdateSuggestions(draft, inputActive || focusInput);
        if (suggestions.Count > 0)
            DrawSuggestions(tab, inputPos);

        if (!submitted)
            return;

        if (Submit(tab, draft))
        {
            drafts[tab.Id] = string.Empty;

            // Match vanilla: sending hands control back to the game (WASD
            // works immediately); Enter re-opens the input.
            ImGuiP.FocusWindow(default);
            suppressEnterUntilReleased = true;
        }
        else
        {
            // Send failed; keep the draft and the focus so it can be fixed.
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    private static readonly string[] LinkPlaceholderTokens = ["<item>", "<flag>", "<status>"];

    private static readonly List<(int Start, int Length)> NoSpans = [];

    private static List<(int Start, int Length)> FindLinkPlaceholders(string draft)
    {
        // Shared empty result for the common case; callers never mutate it.
        if (draft.Length == 0)
            return NoSpans;

        List<(int Start, int Length)> spans = [];

        foreach (var token in LinkPlaceholderTokens)
        {
            var from = 0;
            int index;
            while (from < draft.Length
                   && (index = draft.IndexOf(token, from, StringComparison.Ordinal)) >= 0)
            {
                spans.Add((index, token.Length));
                from = index + token.Length;
            }
        }

        spans.Sort();
        return spans;
    }

    /// <summary>
    /// Repaints the input field's text (drawn transparent by the widget when
    /// a link placeholder is present): normal runs in the regular text color,
    /// placeholders in the link color, plus a caret, honoring the field's
    /// internal horizontal scroll. Must run directly after the InputText so
    /// the item rect and id still refer to it.
    /// </summary>
    private void DrawInputTextOverlay(string draft, List<(int Start, int Length)> spans, bool inputActive)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var pad = ImGui.GetStyle().FramePadding;

        var scrollX = 0f;
        var cursor = -1;
        if (inputActive)
        {
            var id = ImGuiP.GetItemID();
            var state = ImGuiP.GetInputTextState(id);
            if (!state.IsNull && state.ID == id)
            {
                scrollX = state.ScrollX;
                cursor = Math.Clamp(state.Stb.Cursor, 0, draft.Length);
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(
            new Vector2(min.X + pad.X, min.Y),
            new Vector2(max.X - pad.X, max.Y),
            true);

        var normalColor = ImGui.GetColorU32(ImGuiCol.Text);
        var linkColor = ImGui.GetColorU32(ChatColors.Link);
        var pos = new Vector2(min.X + pad.X - scrollX, min.Y + pad.Y);

        void DrawRun(int start, int end, uint color)
        {
            if (end <= start)
                return;

            var run = draft[start..end];
            drawList.AddText(pos, color, run);
            pos.X += ImGui.CalcTextSize(run).X;
        }

        var previous = 0;
        foreach (var (start, length) in spans)
        {
            DrawRun(previous, start, normalColor);
            DrawRun(start, start + length, linkColor);
            previous = start + length;
        }

        DrawRun(previous, draft.Length, normalColor);

        // The widget's own caret is transparent along with its text; draw a
        // replacement. Free-running blink (the real one resets on keypress).
        if (cursor >= 0 && (ImGui.GetTime() % 1.2) <= 0.8)
        {
            var x = min.X + pad.X - scrollX + ImGui.CalcTextSize(draft[..cursor]).X;
            drawList.AddLine(
                new Vector2(x, min.Y + pad.Y),
                new Vector2(x, max.Y - pad.Y),
                normalColor);
        }

        drawList.PopClipRect();
    }

    private static readonly string[] TellCommands = ["/tell", "/t"];

    /// <summary>Trailing ":xx" emote partial with the colon at a word start.</summary>
    private static readonly System.Text.RegularExpressions.Regex EmotePartial =
        new(@"(?:^|\s):([\w+\-]{2,})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void UpdateSuggestions(string draft, bool inputActive)
    {
        // Emote completion fires anywhere in the message, including inside a
        // command's text argument, so it runs before the command logic.
        if (inputActive && plugin.Configuration.RenderEmotes)
        {
            var emote = EmotePartial.Match(draft);
            if (emote.Success)
            {
                if (draft != suggestionQuery)
                {
                    suggestionQuery = draft;
                    var prefix = draft[..emote.Groups[1].Index]; // ends with the colon
                    suggestions = Emotes.Query(emote.Groups[1].Value, 8)
                        .Select(e => new CommandEntry(
                            $"{prefix}{e.Name}:", string.Empty,
                            Display: $":{e.Name}:", Emote: e.Emoji))
                        .ToList();
                    suggestionIndex = 0;
                }

                return;
            }
        }

        var wantSuggestions = inputActive && draft.Length > 1 && draft[0] == '/';

        // "/tell " (or "/t ") switches from command completion to completing
        // the name argument. Once the typed text stops prefixing any known
        // player (i.e. the message part began), the popup disappears on its own.
        string? tellCommand = null;
        var tellPartial = string.Empty;
        if (wantSuggestions)
        {
            foreach (var command in TellCommands)
            {
                if (draft.Length > command.Length
                    && draft[command.Length] == ' '
                    && draft.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                {
                    tellCommand = draft[..command.Length];
                    tellPartial = draft[(command.Length + 1)..];
                    break;
                }
            }

            if (tellCommand == null && draft.Contains(' '))
                wantSuggestions = false;
        }

        if (!wantSuggestions)
        {
            suggestions = [];
            suggestionQuery = string.Empty;
            return;
        }

        if (draft != suggestionQuery)
        {
            suggestionQuery = draft;
            suggestions = tellCommand != null
                ? QueryTellNames(tellCommand, tellPartial)
                : commandIndex.Query(draft);
            suggestionIndex = 0;
        }
    }

    /// <summary>
    /// Name suggestions for a partial "/tell " target, as full commands so
    /// the whole-buffer acceptance path works unchanged. Sources in priority
    /// order: open tell tabs, party, friends, nearby players.
    /// </summary>
    private List<CommandEntry> QueryTellNames(string command, string partial)
    {
        var candidates = new List<(string Key, string Source)>();

        foreach (var partner in tabs.TellPartners())
            candidates.Add((partner, "tell tab"));

        foreach (var member in Plugin.PartyList)
        {
            var name = member.Name.TextValue;
            if (name.Length > 0)
                candidates.Add((GameData.WithWorld(name, member.World.RowId), "party"));
        }

        foreach (var friend in plugin.Presence.FriendNames())
            candidates.Add((friend, "friend"));

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
                continue;

            var name = player.Name.TextValue;
            if (name.Length > 0 && name != localFullName)
                candidates.Add((GameData.WithWorld(name, player.HomeWorld.RowId), "nearby"));
        }

        return candidates
            .Where(c => c.Key.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(c => new CommandEntry($"{command} {c.Key}", c.Source))
            .ToList();
    }

    /// <summary>
    /// Replaces the draft with the highlighted completion (plus a trailing
    /// space) and refocuses the input. Clears the popup and seeds the query so
    /// it doesn't immediately reopen on the completed text.
    /// </summary>
    private void AcceptSuggestion(TabState tab)
    {
        var completed = suggestions[Math.Clamp(suggestionIndex, 0, suggestions.Count - 1)].Command + " ";
        drafts[tab.Id] = completed;
        focusInput = true;
        suggestions = [];
        suggestionQuery = completed;
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

            if (entry.Emote != null && Emotes.GetTexture(entry.Emote) is { } emoteWrap)
            {
                var iconSize = ImGui.GetTextLineHeight();
                ImGui.Image(emoteWrap.Handle, new Vector2(iconSize, iconSize));
                ImGui.SameLine();
            }

            if (ImGui.Selectable($"{entry.Display ?? entry.Command}##sugg{i}", selected))
            {
                suggestionIndex = i;
                AcceptSuggestion(tab);
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
            // A failed translated send handing its text back while the field
            // is still focused. Same reason as the Space acceptance below: an
            // external draft write is ignored once the widget owns its buffer.
            if (restoreDraftRequested is { } restored)
            {
                restoreDraftRequested = null;
                data.DeleteChars(0, data.BufTextLen);
                data.InsertChars(0, restored);
            }

            // Programmatic focus selects the whole buffer; typing would then
            // replace it. Put the cursor at the end with nothing selected.
            if (clearSelection)
            {
                clearSelection = false;
                data.CursorPos = data.BufTextLen;
                data.SelectionStart = data.BufTextLen;
                data.SelectionEnd = data.BufTextLen;
            }

            // Space accepted a completion (flagged in the char filter earlier
            // this same frame). Edit the widget's own buffer here — an external
            // draft write is ignored while the field is active, so it must go
            // through the callback like the Tab path does.
            if (acceptSuggestionRequested)
            {
                acceptSuggestionRequested = false;
                if (suggestions.Count > 0)
                {
                    var completed =
                        suggestions[Math.Clamp(suggestionIndex, 0, suggestions.Count - 1)].Command + " ";
                    data.DeleteChars(0, data.BufTextLen);
                    data.InsertChars(0, completed);
                }
            }

            return 0;
        }

        // In Tab-cycles mode, Space accepts the highlighted completion. The
        // char is discarded here (return 1) and flagged; CallbackAlways fires
        // later this same frame and applies the edit to the widget's buffer.
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCharFilter)
        {
            if (data.EventChar == ' '
                && suggestions.Count > 0
                && plugin.Configuration.TabCyclesSuggestions)
            {
                acceptSuggestionRequested = true;
                return 1;
            }

            return 0;
        }

        // Tab: with text, accept the highlighted suggestion; on an empty
        // input, cycle chats (fixed-destination tabs) or the game's active
        // channel (General/System), which updates the border indicator.
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (suggestions.Count > 0)
            {
                // Tab-cycles mode: move the highlight (Shift = backward) and
                // wait for Space to lock it in, rather than accepting now.
                if (plugin.Configuration.TabCyclesSuggestions)
                {
                    var direction = ImGui.GetIO().KeyShift ? -1 : 1;
                    suggestionIndex =
                        (suggestionIndex + direction + suggestions.Count) % suggestions.Count;
                    return 0;
                }

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

        // A recalled command must not pop the suggestion box, or the next
        // Up/Down moves its highlight instead of walking history. Seeding
        // the query keeps it suppressed until the text is actually edited.
        suggestions = [];
        suggestionQuery = replacement;
        return 0;
    }

    /// <summary>
    /// True when the draft was consumed (clear it and release focus), false to
    /// keep both so the user can fix it. A send needing outgoing translation
    /// reports consumed immediately and finishes asynchronously.
    /// </summary>
    private bool Submit(TabState tab, string draft)
    {
        var text = draft.Trim();
        if (text.Length == 0)
            return true;

        // Commands address the game, not a person, so they are never
        // translated and keep the synchronous path exactly as it was.
        if (text[0] != '/' && plugin.Configuration.TranslateOutgoing)
        {
            // Rejected rather than queued: translations complete out of order,
            // so a queue could put a later line on the wire before an earlier
            // one, and the window being contested is a single round trip.
            if (pendingSend is { IsCompleted: false })
            {
                Notify("Still translating the previous message.");
                return false;
            }

            // Recorded here, on the draw thread, and from the ORIGINAL text:
            // history is what the user typed, not what went out.
            RecordHistory(text);
            pendingSendTabId = tab.Id;
            pendingSend = SendTranslatedAsync(tab, text);
            return true;
        }

        if (!ChatSender.Send(text[0] == '/' ? text : ApplyDestination(tab, text)))
        {
            // The only false path is the length cap — easy to hit in a tell
            // tab, where "/tell Name@World " is prepended after the input's
            // own limit. Without feedback, Enter appears to do nothing.
            Notify("Message too long to send.");
            return false;
        }

        RecordHistory(text);
        return true;
    }

    /// <summary>Prefixes the tab's destination onto a plain (non-command) message.</summary>
    private static string ApplyDestination(TabState tab, string text) =>
        tab.IsTell
            ? $"/tell {tab.TellPartner} {text}"
            : tab.SendCommand is { Length: > 0 } sendCommand
                ? $"{sendCommand} {text}"
                : text;

    private void RecordHistory(string text)
    {
        if (sentHistory.Count == 0 || sentHistory[^1] != text)
        {
            sentHistory.Add(text);
            if (sentHistory.Count > MaxHistory)
                sentHistory.RemoveAt(0);
        }

        historyPos = -1;
        historyStash = string.Empty;
    }

    // Past this the input has been sitting empty long enough that a message
    // finally going out is more surprising than one that never did.
    private static readonly TimeSpan OutgoingTranslateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Translates a typed line and sends the translation in its place (the
    /// original is not appended). Runs off the draw thread, so a failure hands
    /// the text back through <see cref="pendingDraftRestore"/> rather than
    /// touching the drafts, the history or the notification queue from here.
    /// </summary>
    private async Task SendTranslatedAsync(TabState tab, string text)
    {
        string? failure;
        try
        {
            using var cts = new CancellationTokenSource(OutgoingTranslateTimeout);
            var translated = await plugin.Translation
                .TranslateOutgoingAsync(text, cts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(translated))
            {
                // The provider's own reason when there is one: the common case
                // is outgoing translation switched on with no API key behind
                // it, and "translation failed" alone gives nothing to act on.
                failure = plugin.Translation.LastError is { Length: > 0 } reason
                    ? $"Not sent — {reason}"
                    : "Translation failed; message not sent.";
            }
            else
            {
                // Send re-checks the UTF-8 cap and false means only that —
                // the check that matters here, because a translation is easily
                // longer than what was typed and the prefix lands on top of it.
                failure = ChatSender.Send(ApplyDestination(tab, translated))
                    ? null
                    : "Translation too long to send.";
            }
        }
        catch (OperationCanceledException)
        {
            failure = "Translation timed out; message not sent.";
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Outgoing translation failed");
            failure = "Translation failed; message not sent.";
        }

        // Published before this method completes, so the draw thread can never
        // observe pendingSend as finished without also seeing the restore.
        if (failure != null)
            pendingDraftRestore = new PendingRestore(tab.Id, text, failure);
    }

    private void DrawMessage(Message message, int repeats = 1)
    {
        // Display strings are cached on the message: this runs for up to
        // MaxRenderedMessages rows every frame and all inputs are immutable
        // (the clock format tags its cache and rebuilds on change).
        var use24h = plugin.Configuration.Use24HourClock;
        if (message.StampCache == null || message.StampCache24h != use24h)
        {
            message.StampCache24h = use24h;
            message.StampCache = use24h
                ? $"[{message.Timestamp:HH:mm}]"
                : $"[{message.Timestamp:h:mm tt}]";
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            if (FFTheme.GameLayout)
                VanillaChrome.ShadowAtCursor(message.StampCache);
            ImGui.TextUnformatted(message.StampCache);
        }

        // Masked: flagged variants (source bits in the high byte) must color
        // and prefix like their base kind — Route already matches them into
        // the same tabs.
        var channelColor = ChatColors.For(ChatTypes.Mask(message.Type));

        if (!message.HasPrefixCache)
        {
            message.HasPrefixCache = true;
            message.PrefixCache = BuildPrefixSegments(message);
        }

        if (message.PrefixCache is { Count: > 0 } prefixSegments)
        {
            var prefixColor = channelColor;
            if (message.SenderJob is { } job)
            {
                if (plugin.Configuration.JobIconPartyNames)
                    DrawJobIcon(job);
                if (plugin.Configuration.RoleColorPartyNames && RoleColor(job) is { } roleColor)
                    prefixColor = roleColor;
            }

            foreach (var segment in prefixSegments)
            {
                // Icon segments (the cross-world glyph) have empty text; draw
                // the glyph, or skip it if the texture isn't ready this frame.
                if (segment.IconId != 0)
                {
                    DrawIconToken(segment);
                    continue;
                }

                DrawSegment(segment, segment.Color ?? prefixColor);
            }
        }

        // Read once per draw: the field is published by the translation worker
        // and can change between two reads inside the same frame.
        var translation = message.Translation;

        if (translation is { Status: TranslationStatus.Done, Text: { } translated })
        {
            // The translated body carries no links, emotes or game colors — it
            // replaces the segments as one flat run in the translation color.
            // Everything before it (timestamp, job icon, prefix) is untouched.
            var body = TranslatedBodySegment(message, translation, translated);
            var color = TranslationService.TranslationColor(plugin.Configuration);
            if (DrawTranslatedBody(body, color) && plugin.Configuration.ShowTranslationTooltip)
                pendingTranslationTooltip = (message, translation);
        }
        else if (message.Segments.Count > 0)
        {
            foreach (var segment in message.Segments)
            {
                // Icon segments have empty text: draw the glyph, or skip the
                // segment entirely if the texture isn't ready this frame.
                if (segment.IconId != 0)
                {
                    DrawIconToken(segment);
                    continue;
                }

                if (segment.Emote != null && plugin.Configuration.RenderEmotes
                    && DrawEmoteToken(segment))
                    continue;

                // Item/map links stand out even when the game didn't color
                // them itself, so they read as clickable.
                var fallback = segment.Link is SegmentLink.Item or SegmentLink.Map or SegmentLink.Url or SegmentLink.Achievement or SegmentLink.PartyFinder or SegmentLink.Quest or SegmentLink.Dalamud
                    ? ChatColors.Link
                    : channelColor;
                DrawSegment(segment, segment.Color ?? fallback);
            }
        }
        else
        {
            DrawSegment(message.FallbackCache ??= new MessageSegment(message.Text, null, null), channelColor);
        }

        if (repeats > 1)
            DrawSegmentText($" ×{repeats}", ChatColors.Timestamp, null);
    }

    // Hovered translated body waiting for its tooltip, deferred to the end of
    // the log: submitting a tooltip window overwrites ImGui's last-item rect,
    // which the row still needs for the ×N counter and the mention highlight.
    // At most one row is hovered, so a single slot is enough.
    private (Message Message, TranslationState Translation)? pendingTranslationTooltip;

    // Render cache for translated bodies. It lives here rather than on Message
    // (which owns the caches derived from its own immutable fields): the entry
    // is keyed on the TranslationState instance so a re-translation rebuilds
    // it, and the weak table drops it when the message is evicted.
    private readonly ConditionalWeakTable<Message, TranslatedBody> translatedBodies = new();

    private sealed class TranslatedBody
    {
        public TranslationState? Source;
        public MessageSegment? Segment;
    }

    /// <summary>
    /// The translated body as a segment, so it word-wraps through the same
    /// token machinery as normal text. Built once per translation — this runs
    /// for every visible translated row, every frame.
    /// </summary>
    private MessageSegment TranslatedBodySegment(Message message, TranslationState translation, string text)
    {
        var cache = translatedBodies.GetOrCreateValue(message);

        // Identity, not record equality: `!=` on a record is a value compare
        // that would run for every visible translated row every frame, and a
        // fresh state instance is exactly what "the translation changed" means.
        if (!ReferenceEquals(cache.Source, translation))
        {
            cache.Source = translation;
            cache.Segment = new MessageSegment(text, null, null);
        }

        return cache.Segment!;
    }

    /// <summary>
    /// Draws a translated body as one flat run, reporting whether any of its
    /// word tokens is hovered. Hover is accumulated here instead of inside
    /// DrawToken, which owns the link hover handling normal segments depend on:
    /// the body is many ImGui items but must show a single tooltip.
    /// </summary>
    private bool DrawTranslatedBody(MessageSegment segment, Vector4 color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var hovered = false;
        var forceNewLine = false;
        foreach (var token in segment.Tokens ??= BuildTokens(segment.Text))
        {
            if (token == "\n")
            {
                forceNewLine = true;
                continue;
            }

            // No link, so DrawToken returns before its own hover test — this
            // stays the only IsItemHovered call for the token.
            DrawToken(token, null, forceNewLine);
            hovered |= ImGui.IsItemHovered();
            forceNewLine = false;
        }

        return hovered;
    }

    /// <summary>
    /// Hover tooltip for a translated body: the original line under a dim
    /// "JA → Japanese" header, or just "original" when either end is unknown.
    /// </summary>
    private static void DrawTranslationTooltip(Message message, TranslationState translation)
    {
        using var tooltip = ImRaii.Tooltip();

        var header = translation.DetectedSource is { Length: > 0 } source
                     && translation.TargetLanguage is { Length: > 0 } target
            ? $"{source} → {Languages.Label(target)}"
            : "original";

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted(header);
        }

        // Chat lines run long; unwrapped, the tooltip stretches off-screen.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 400);
        ImGui.TextUnformatted(message.Text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Inline Twemoji image at text height, continuing the line like a word
    /// token. False while the texture isn't ready (pending GPU upload, or it
    /// failed to load), so the ":shortcode:" text draws instead.
    /// </summary>
    private static bool DrawEmoteToken(MessageSegment segment)
    {
        var wrap = Emotes.GetTexture(segment.Emote!);
        if (wrap == null)
            return false;

        var size = ImGui.GetTextLineHeight();
        ContinueLineIfFits(size);
        ImGui.Image(wrap.Handle, new Vector2(size, size));

        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.Image(wrap.Handle, new Vector2(size * 3, size * 3));
            ImGui.TextUnformatted(segment.Text);
        }

        return true;
    }

    /// <summary>
    /// Inline game bitmap-font icon (gil, HQ, element glyphs, etc.) at text
    /// height, continuing the line like a word token. False while the atlas
    /// entry or texture isn't ready; the icon has no text, so nothing draws.
    /// </summary>
    private static bool DrawIconToken(MessageSegment segment)
    {
        if (!GameIcons.TryGetEntry(segment.IconId, out var entry))
            return false;

        var tex = Plugin.TextureProvider.GetFromGame("common/font/fonticon_ps5.tex").GetWrapOrDefault();
        if (tex == null)
            return false;

        var texSize = new Vector2(tex.Width, tex.Height);
        var lineHeight = ImGui.GetTextLineHeight();
        var ratio = lineHeight / entry.Height;
        var size = new Vector2(entry.Width, entry.Height) * ratio;

        // fonticon_ps5.tex is the hi-res (2x) sheet; the +170 row offset and the
        // *2 scaling on the atlas rect are specific to it (per ChatTwo).
        var uv0 = new Vector2(entry.Left, entry.Top + 170) * 2 / texSize;
        var uv1 = new Vector2(entry.Left + entry.Width, entry.Top + entry.Height + 170) * 2 / texSize;

        ContinueLineIfFits(size.X);
        ImGui.Image(tex.Handle, size, uv0, uv1);
        return true;
    }

    /// <summary>Framed job icon (62100 block) at text height, continuing the line.</summary>
    private static void DrawJobIcon(uint jobId)
    {
        var wrap = Plugin.TextureProvider
            .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(62100 + jobId))
            .GetWrapOrDefault();
        if (wrap == null)
            return;

        ImGui.SameLine(0, 4);
        var size = ImGui.GetTextLineHeight();
        ImGui.Image(wrap.Handle, new Vector2(size, size));
    }

    /// <summary>ClassJob.Role: 1 tank, 2 melee, 3 ranged, 4 healer, 0 hand/land.</summary>
    private static Vector4? RoleColor(uint jobId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(jobId, out var row))
            return null;

        return row.Role switch
        {
            1 => new Vector4(0.25f, 0.55f, 0.95f, 1f),
            4 => new Vector4(0.35f, 0.80f, 0.42f, 1f),
            2 or 3 => new Vector4(0.93f, 0.35f, 0.35f, 1f),
            _ => null,
        };
    }

    /// <summary>
    /// Draws a segment continuing the current chat line, word-wrapping
    /// against the window edge; tokens come from the segment's cache.
    /// Assumes the previous ImGui item is the preceding chunk of this same
    /// line (the timestamp starts every line).
    /// </summary>
    private void DrawSegment(MessageSegment segment, Vector4 color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var forceNewLine = false;
        foreach (var token in segment.Tokens ??= BuildTokens(segment.Text))
        {
            if (token == "\n")
            {
                forceNewLine = true;
                continue;
            }

            DrawToken(token, segment.Link, forceNewLine);
            forceNewLine = false;
        }
    }

    /// <summary>
    /// Uncached variant of <see cref="DrawSegment"/> for one-off strings
    /// (the ×N repeat counter).
    /// </summary>
    private void DrawSegmentText(string text, Vector4 color, SegmentLink? link)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var forceNewLine = false;
        foreach (var token in BuildTokens(text))
        {
            if (token == "\n")
            {
                forceNewLine = true;
                continue;
            }

            DrawToken(token, link, forceNewLine);
            forceNewLine = false;
        }
    }

    /// <summary>SameLine-continues the current chat line if the width fits before the window edge.</summary>
    private static void ContinueLineIfFits(float width)
    {
        var lastEnd = ImGui.GetItemRectMax().X;
        var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (lastEnd + width <= rightEdge)
            ImGui.SameLine(0, 0);
    }

    private void DrawToken(string token, SegmentLink? link, bool forceNewLine)
    {
        if (!forceNewLine)
            ContinueLineIfFits(ImGui.CalcTextSize(token).X);

        // The game's log has no panel behind it, so every glyph carries a hard
        // drop shadow. Queued into the draw list first; ImGui paints the real
        // text over it and layout is untouched.
        if (FFTheme.GameLayout)
            VanillaChrome.ShadowAtCursor(token);

        ImGui.TextUnformatted(token);

        if (link == null || !ImGui.IsItemHovered())
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        switch (link)
        {
            case SegmentLink.Item item:
                if (plugin.Configuration.NativeItemTooltips)
                {
                    var rawId = item.ItemId + (item.Hq ? 1_000_000u : 0u);
                    nativeTooltipGrace = 2;
                    if (nativeTooltipItem != rawId && NativeItemTooltip.Open(rawId))
                        nativeTooltipItem = rawId;
                    if (nativeTooltipItem == rawId)
                        NativeItemTooltip.Reposition(mainWindowPos, mainWindowSize);
                    else
                        DrawItemTooltip(item);
                }
                else
                {
                    DrawItemTooltip(item);
                }

                if (clicked || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    contextItem = item;
                    linkClaimedRightClick |= ImGui.IsMouseClicked(ImGuiMouseButton.Right);
                    ImGui.OpenPopup(itemContextPopupId);
                }

                break;

            case SegmentLink.Map map:
                ImGui.SetTooltip("Click: open map");
                if (clicked)
                    Plugin.GameGui.OpenMapWithMapLink(map.Payload);
                break;

            case SegmentLink.Url url:
                ImGui.SetTooltip($"{url.Target}\nClick: open in browser");
                if (clicked)
                    Dalamud.Utility.Util.OpenLink(url.Target);
                break;

            case SegmentLink.Achievement achievement:
                ImGui.SetTooltip("Click: open achievement");
                if (clicked)
                    AchievementActions.Open(achievement.Id);
                break;

            case SegmentLink.PartyFinder partyFinder:
                ImGui.SetTooltip("Click: open Party Finder");
                if (clicked)
                {
                    if (partyFinder.Notification)
                        PartyFinderActions.OpenWindow();
                    else
                        PartyFinderActions.OpenListing(partyFinder.ListingId);
                }

                break;

            case SegmentLink.Quest quest:
                ImGui.SetTooltip("Click: open in journal");
                if (clicked)
                    QuestActions.Open(quest.QuestRowId);
                break;

            case SegmentLink.Status status:
                ImGui.SetTooltip(StatusName(status.StatusId));
                break;

            case SegmentLink.Dalamud dalamud:
                ImGui.SetTooltip("Click: follow link");
                if (clicked)
                    ChatLinkActions.Invoke(dalamud.Payload);
                break;

            case SegmentLink.Player player:
                ImGui.SetTooltip($"{player.Partner}\nClick: open tell tab — right-click: menu");
                if (clicked)
                {
                    var tellTab = tabs.OpenTellTab(player.Partner);
                    selectTabId = tellTab.Id;
                    focusInput = true;
                }

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    contextPartner = player.Partner;
                    linkClaimedRightClick = true;
                    ImGui.OpenPopup(logContextPopupId);
                }

                break;
        }
    }

    /// <summary>Status effect name from Lumina, falling back to an id string.</summary>
    private static string StatusName(uint statusId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(statusId, out var row)
        && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"Status #{statusId}";

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

    /// <summary>
    /// Token list for a piece of chat text: words keeping their trailing
    /// spaces, with "\n" entries marking forced line breaks.
    /// </summary>
    private static string[] BuildTokens(string text)
    {
        List<string> tokens = [];
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            foreach (var token in Tokenize(newline < 0 ? text[start..] : text[start..newline]))
                tokens.Add(token);

            if (newline < 0)
                break;
            tokens.Add("\n");
            start = newline + 1;
        }

        return [.. tokens];
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

    /// <summary>Opening/closing affixes around the sender name per channel, or null for no prefix.</summary>
    private static (string Open, string Close)? PrefixDecoration(Message message)
    {
        if (message.Sender.Length == 0)
            return null;

        // Masked for the same reason as the channel color lookup.
        return ChatTypes.Mask(message.Type) switch
        {
            XivChatType.TellIncoming => ("", " >>"),
            XivChatType.TellOutgoing => (">> ", ":"),
            XivChatType.Party or XivChatType.CrossParty => ("(", ")"),
            XivChatType.Alliance => ("((", "))"),
            // Emote messages already contain the player's name in the text.
            XivChatType.CustomEmote or XivChatType.StandardEmote => null,
            XivChatType.FreeCompany => ("[FC]<", ">"),
            _ => ("", ":"),
        };
    }

    /// <summary>
    /// Builds the sender prefix as styled segments. The sender is parsed from
    /// its raw SeString (not the flattened text) so a cross-world player's
    /// world icon survives and their name stays separated from the world it
    /// precedes. Returns null for channels that carry no prefix.
    /// </summary>
    private static IReadOnlyList<MessageSegment>? BuildPrefixSegments(Message message)
    {
        if (PrefixDecoration(message) is not { } decoration)
            return null;

        var (open, close) = decoration;
        var link = message.SenderPlayer != null ? new SegmentLink.Player(message.SenderPlayer) : null;
        var segments = new List<MessageSegment>();

        if (open.Length > 0)
            segments.Add(new MessageSegment(open, null, link));

        // Parsed sender segments: text runs inherit the player link so the
        // whole prefix is one click target; icon segments (the cross-world
        // glyph) pass through so DrawIconToken renders them.
        var sender = Dalamud.Game.Text.SeStringHandling.SeString.Parse(message.SenderRaw);
        foreach (var segment in MessageParser.Parse(sender))
            segments.Add(segment.Link == null ? segment with { Link = link } : segment);

        segments.Add(new MessageSegment(close + " ", null, link));
        return segments;
    }
}
