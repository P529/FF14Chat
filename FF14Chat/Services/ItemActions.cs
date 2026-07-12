using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FF14Chat.Services;

/// <summary>Native game actions for an item link's context menu.</summary>
public static unsafe class ItemActions
{
    public static void TryOn(uint itemId) => AgentTryon.TryOn(0xFF, itemId, 0);

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
