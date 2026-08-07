using System;
using Dalamud.Game.Gui.ContextMenu;

namespace FF14Chat.Services;

/// <summary>
/// Adds "Try On Original" to the game's own item context menus. The native
/// Try On previews the item as it is glamoured and dyed; this one previews the
/// item's actual model, which is the whole point of trying it on.
/// </summary>
public sealed class GameContextMenu : IDisposable
{
    private readonly Configuration configuration;

    public GameContextMenu(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!configuration.TryOnOriginalMenuItem)
            return;

        // Glamour only lives on a real inventory item. Chat links, market
        // board rows and recipe notes are ContextMenuType.Default and have
        // nothing to strip, so the entry would be meaningless there.
        if (args.MenuType != ContextMenuType.Inventory)
            return;

        if (args.Target is not MenuTargetInventory { TargetItem: { } item })
            return;

        // Nothing glamoured over it means the native Try On is already the
        // original, and a duplicate entry is pure clutter in a menu that gets
        // opened constantly.
        if (item.GlamourId == 0)
            return;

        // BaseItemId strips the HQ/collectable offset off the raw id.
        var itemId = item.BaseItemId;
        if (itemId == 0)
            return;

        // Same equippability test the chat-link menu uses; also excludes
        // facewear, which lives in a different sheet entirely.
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var row)
            || row.EquipSlotCategory.RowId == 0)
        {
            return;
        }

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
}
