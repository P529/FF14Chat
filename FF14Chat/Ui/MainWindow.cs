using System;
using System.Collections.Generic;
using System.Linq;
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
    private bool suppressEnterUntilReleased;

    private readonly CommandIndex commandIndex = new();
    private List<CommandEntry> suggestions = [];
    private int suggestionIndex;
    private string suggestionQuery = string.Empty;

    private string? selectTabId;

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
        ScheduleTellTabSync(includeStaged: false);
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
                world = ResolveWorldName(worldId);

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

    private static string ResolveWorldName(ushort worldId)
    {
        return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().TryGetRow(worldId, out var world)
            ? world.Name.ExtractText()
            : string.Empty;
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
                    world = ResolveWorldName(worldId);

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
        changeChannelNameHook.Dispose();
        chatLogActivateHook?.Dispose();
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

        localFullName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        var firstSpace = localFullName.IndexOf(' ');
        localFirstName = firstSpace > 0 ? localFullName[..firstSpace] : string.Empty;

        // Hashed at window-root scope so tab items can open it from within
        // their own ID scope.
        tabContextPopupId = ImGui.GetID(PlayerContextPopup);

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
            // spaces reserve room for the badge; tell tabs lead with spaces
            // for the presence dot.
            var showPresence = tab.IsTell && plugin.Configuration.ShowTellPresence;
            var label = showPresence
                ? $"  {tab.Title}  ###{tab.Id}"
                : $"{tab.Title}  ###{tab.Id}";
            var itemFlags = selectTabId == tab.Id ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

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
        DrawPlayerContextMenu();
        selectTabId = null;
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
            if (ImGui.Selectable("Target"))
                PlayerActions.Target(nearby!);
            if (ImGui.Selectable("Examine"))
                PlayerActions.Examine(nearby!);
            if (ImGui.Selectable("Adventurer Plate"))
                PlayerActions.OpenAdventurerPlate(nearby!);
        }

        if (nearby == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Only available while the player is nearby.");

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
        var status = plugin.Presence.StatusFor(tab.TellPartner!);
        var color = status switch
        {
            PresenceStatus.Online => new Vector4(0.35f, 0.85f, 0.40f, 1f),
            PresenceStatus.Afk => new Vector4(0.90f, 0.30f, 0.25f, 1f),
            PresenceStatus.Offline => new Vector4(0.55f, 0.55f, 0.55f, 0.90f),
            _ => new Vector4(0.35f, 0.55f, 0.95f, 0.90f),
        };

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = ImGui.GetFontSize() * 0.18f;
        var center = new Vector2(min.X + radius + 5f, (min.Y + max.Y) / 2f + 1f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.GetColorU32(color), 12);
    }

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
        // Hashed at the log child's root scope; DrawToken opens it from
        // within message rows, DrawPlayerContextMenu below begins it here.
        logContextPopupId = ImGui.GetID(PlayerContextPopup);

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
        {
            if (i > first && messages[i - 1].Timestamp.Date != messages[i].Timestamp.Date)
                DrawDateSeparator(messages[i].Timestamp);

            var lineStart = ImGui.GetCursorScreenPos();
            var lineWidth = ImGui.GetContentRegionAvail().X;
            DrawMessage(messages[i]);

            if (IsMention(messages[i]))
            {
                var bottom = ImGui.GetItemRectMax().Y;
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(
                    lineStart, new Vector2(lineStart.X + lineWidth, bottom),
                    ImGui.GetColorU32(FFTheme.Gold with { W = 0.09f }));
                drawList.AddRectFilled(
                    lineStart - new Vector2(4f, 0f), new Vector2(lineStart.X - 2f, bottom),
                    ImGui.GetColorU32(FFTheme.GoldBright with { W = 0.85f }));
            }
        }

        DrawPlayerContextMenu();

        if (pinnedToBottom && newMessages)
            ImGui.SetScrollHereY(1f);
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
        var shell = RaptureShellModule.Instance();
        if (agent == null || shell == null)
            return;

        var index = Array.IndexOf(ChannelCycle, (int)agent->CurrentChannel);
        var next = ChannelCycle[(index + direction + ChannelCycle.Length) % ChannelCycle.Length];

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
            // emote command from a UI button).
            if (pendingInsert is { } insert)
            {
                pendingInsert = null;
                if (draft.Length == 0)
                    draft = insert;
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

    private static readonly string[] TellCommands = ["/tell", "/t"];

    private void UpdateSuggestions(string draft, bool inputActive)
    {
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
                candidates.Add((PresenceTracker.WithWorld(name, member.World.RowId), "party"));
        }

        foreach (var friend in plugin.Presence.FriendNames())
            candidates.Add((friend, "friend"));

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
                continue;

            var name = player.Name.TextValue;
            if (name.Length > 0 && name != localFullName)
                candidates.Add((PresenceTracker.WithWorld(name, player.HomeWorld.RowId), "nearby"));
        }

        return candidates
            .Where(c => c.Key.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(c => new CommandEntry($"{command} {c.Key}", c.Source, false))
            .ToList();
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
                    ImGui.OpenPopup(logContextPopupId);
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
