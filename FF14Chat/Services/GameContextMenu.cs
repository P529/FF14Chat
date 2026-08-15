using System;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FF14Chat.Services;

/// <summary>
/// Adds "Try On Original" to the game's own item context menus. The native
/// Try On previews the item as it is glamoured and dyed; this one previews the
/// item's actual model, which is the whole point of trying it on.
///
/// Also owns the optional Ctrl+right-click shortcut, which skips the menu
/// entirely and tries the item on directly.
///
/// Two very different menus are involved. Inventory-ish windows (bags, armoury,
/// character sheet, retainers) route through <see cref="AgentInventoryContext"/>
/// and arrive as <see cref="ContextMenuType.Inventory"/>. The examine window
/// owns its gear itself: <see cref="AgentInspect"/> holds the examined items and
/// the clicked slot, and its menu opens through <see cref="AgentContext"/> like
/// any plain menu, so nothing about the inventory path applies to it.
/// </summary>
public sealed unsafe class GameContextMenu : IDisposable
{
    /// <summary>The examine window. Its gear rows are another player's items.</summary>
    private const string ExamineAddon = "CharacterInspect";

    private delegate void OpenForItemSlotDelegate(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

    private delegate void OpenContextMenuForAddonDelegate(
        AgentContext* agent, uint ownerAddonId, bool bindToOwner);

    private readonly Configuration configuration;
    private readonly Hook<OpenForItemSlotDelegate>? openForItemSlotHook;
    private readonly Hook<OpenContextMenuForAddonDelegate>? openContextMenuForAddonHook;

    public GameContextMenu(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;

        // Both hooks exist for one reason: they are the last point at which a
        // menu can be suppressed. Dalamud's OnMenuOpened fires once the menu is
        // already committed to opening, so the Ctrl shortcut can't live there.
        // A patch-day break costs the shortcut, not the menu entry.
        try
        {
            openForItemSlotHook = Plugin.GameInterop.HookFromAddress<OpenForItemSlotDelegate>(
                (nint)AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot,
                OpenForItemSlotDetour);
            openForItemSlotHook.Enable();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "AgentInventoryContext.OpenForItemSlot hook failed; Ctrl+right-click try-on is off in inventories");
        }

        try
        {
            openContextMenuForAddonHook = Plugin.GameInterop.HookFromAddress<OpenContextMenuForAddonDelegate>(
                (nint)AgentContext.MemberFunctionPointers.OpenContextMenuForAddon,
                OpenContextMenuForAddonDetour);
            openContextMenuForAddonHook.Enable();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "AgentContext.OpenContextMenuForAddon hook failed; Ctrl+right-click try-on is off in the examine window");
        }
    }

    public void Dispose()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
        openForItemSlotHook?.Dispose();
        openContextMenuForAddonHook?.Dispose();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!configuration.TryOnOriginalMenuItem)
            return;

        if (Resolve(args) is not { } itemId)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Try On Original",
            UseDefaultPrefix = true,
            // Priority sorts descending, so a large positive value lands us
            // below the game's own entries.
            Priority = 1000,
            OnClicked = _ => ItemActions.TryOnOriginal(itemId),
        });
    }

    /// <summary>
    /// The item this menu is about, or null when the entry doesn't belong on
    /// it (not an item menu, not equippable, or nothing to strip).
    /// </summary>
    private static uint? Resolve(IMenuOpenedArgs args)
    {
        // The examine window first: its menu is a plain AgentContext one, so
        // the inventory tests below would all miss it.
        if (args.AddonName == ExamineAddon)
            return ExamineItem();

        // Glamour only lives on a real inventory item. Chat links, market
        // board rows and recipe notes are ContextMenuType.Default and have
        // nothing to strip, so the entry would be meaningless there.
        if (args.MenuType != ContextMenuType.Inventory)
            return null;

        if (args.Target is not MenuTargetInventory { TargetItem: { } item })
            return null;

        // Nothing glamoured over it means the native Try On is already the
        // original, and a duplicate entry is pure clutter in a menu that gets
        // opened constantly.
        if (item.GlamourId == 0)
            return null;

        // BaseItemId strips the HQ/collectable offset off the raw id.
        var itemId = item.BaseItemId;
        return ItemActions.Equippable(itemId) ? itemId : null;
    }

    /// <summary>
    /// The examined gear row that was clicked. AgentInspect keeps the real
    /// items and the glamours the other player is wearing in separate arrays;
    /// the real one is what "original" means here. No glamour test: the
    /// examine window's own Try On previews the glamoured, dyed appearance
    /// either way, so the entry always has something to strip.
    /// </summary>
    private static uint? ExamineItem()
    {
        var agent = AgentInspect.Instance();
        if (agent == null)
            return null;

        var slot = agent->SelectedItemSlot;
        var items = agent->Items;
        if (slot < 0 || slot >= items.Length)
            return null;

        var itemId = BaseItemId(items[slot].ItemId);
        Plugin.Log.Verbose("Examine gear: slot={Slot} item={Item}", slot, itemId);
        return itemId != 0 && ItemActions.Equippable(itemId) ? itemId : null;
    }

    /// <summary>
    /// Ctrl+right-click in an inventory-ish window: try the item on and
    /// swallow the menu. Runs on the game thread, before the menu exists.
    /// </summary>
    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        try
        {
            if (CtrlHeld
                && ItemInSlot(inventoryType, slot) is { } itemId
                && ItemActions.Equippable(itemId))
            {
                ItemActions.TryOnOriginal(itemId);
                return;
            }
        }
        catch (Exception e)
        {
            // Never let our shortcut cost the player their context menu.
            Plugin.Log.Error(e, "Ctrl+right-click try-on failed");
        }

        openForItemSlotHook!.Original(agent, inventoryType, slot, a4, addonId);
    }

    /// <summary>
    /// The same shortcut for the examine window, which opens a plain menu.
    /// Gated on the owning addon id so only the examine window's own menus can
    /// ever be swallowed — a player menu opened while examining is untouched.
    /// </summary>
    private void OpenContextMenuForAddonDetour(AgentContext* agent, uint ownerAddonId, bool bindToOwner)
    {
        try
        {
            if (CtrlHeld
                && ownerAddonId != 0
                && ownerAddonId == AddonId(ExamineAddon)
                && ExamineItem() is { } itemId)
            {
                ItemActions.TryOnOriginal(itemId);
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Ctrl+right-click try-on failed in the examine window");
        }

        openContextMenuForAddonHook!.Original(agent, ownerAddonId, bindToOwner);
    }

    private bool CtrlHeld =>
        configuration.CtrlRightClickTryOn && Plugin.KeyState[VirtualKey.CONTROL];

    /// <summary>Addon id of a named window, or 0 when it isn't open.</summary>
    private static uint AddonId(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon == null ? 0u : addon->Id;
    }

    /// <summary>Base item id of the container slot the menu was opened on.</summary>
    private static uint? ItemInSlot(InventoryType inventoryType, int slot)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        var container = manager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded)
            return null;

        var item = container->GetInventorySlot(slot);
        return item == null || item->ItemId == 0 ? null : BaseItemId(item->ItemId);
    }

    /// <summary>Strips the HQ (+1,000,000) and collectable (+500,000) offsets.</summary>
    private static uint BaseItemId(uint itemId) => itemId switch
    {
        >= 1_000_000 and < 2_000_000 => itemId - 1_000_000,
        >= 500_000 and < 1_000_000 => itemId - 500_000,
        _ => itemId,
    };
}
