using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FF14Chat.Services;

/// <summary>Native game actions for a Party Finder link.</summary>
public static unsafe class PartyFinderActions
{
    public static void OpenListing(uint listingId)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
            agent->OpenListing(listingId);
    }

    /// <summary>Opens the Party Finder window (the "N parties recruiting"
    /// notification target). Deliberately avoids the raw-offset toggle dance —
    /// Show() on an inactive agent is patch-stable; an already-open window is
    /// left as-is.</summary>
    public static void OpenWindow()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent != null && !agent->IsAgentActive())
            agent->Show();
    }
}
