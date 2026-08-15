using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FF14Chat.Services;

/// <summary>Native game actions for an item link's context menu.</summary>
public static unsafe class ItemActions
{
    public static void TryOn(uint itemId) => AgentTryon.TryOn(0xFF, itemId, 0);

    /// <summary>
    /// Try-on preview of the item's own model: no glamour, no dye. The game's
    /// native Try On passes the item's glamour id and stains here; we pass
    /// zeroes, which is what makes this "original".
    /// </summary>
    public static void TryOnOriginal(uint itemId)
    {
        try
        {
            AgentTryon.TryOn(0xFF, itemId, 0, 0, 0, false);
        }
        catch (System.Exception e)
        {
            // A patch-day signature break should say so rather than silently
            // do nothing, same as ChatSender.
            Plugin.Log.Error(e, "Try On Original failed for item {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Whether the item occupies a gear slot, i.e. whether trying it on means
    /// anything. Also excludes facewear, which lives in a different sheet.
    /// </summary>
    public static bool Equippable(uint itemId) =>
        itemId is > 0 and < 500_000
        && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var row)
        && row.EquipSlotCategory.RowId != 0;

    public static void Compare(uint itemId)
    {
        var agent = AgentItemComp.Instance();
        if (agent != null)
            agent->CompareItem(0x4D, itemId, 0, 0);
    }

    public static void SearchRecipes(uint itemId)
    {
        var agent = AgentRecipeProductList.Instance();
        if (agent != null)
            agent->SearchForRecipesUsingItem(itemId);
    }

    public static void SearchForItem(uint itemId)
    {
        var module = ItemFinderModule.Instance();
        if (module != null)
            module->SearchForItem(itemId);
    }

    /// <summary>Stages the item on AgentChatLog; the game then fires the
    /// chat-activation insert event our hook routes into the input.</summary>
    public static void Link(uint itemId)
    {
        var agent = AgentChatLog.Instance();
        if (agent != null)
            agent->LinkItem(itemId);
    }
}
