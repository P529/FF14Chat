using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FF14Chat.Services;

/// <summary>Native game action for an achievement link: opens the Achievement window.</summary>
public static unsafe class AchievementActions
{
    public static void Open(uint id)
    {
        var agent = AgentAchievement.Instance();
        if (agent != null)
            agent->OpenById(id);
    }
}
