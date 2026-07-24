using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FF14Chat.Services;

/// <summary>Native game action for a quest link: opens it in the journal.</summary>
public static unsafe class QuestActions
{
    public static void Open(uint questRowId)
    {
        // Quest sheet rows are 0x10000 + journal number; the journal wants the
        // low 16 bits.
        var agent = AgentQuestJournal.Instance();
        if (agent != null)
            agent->OpenForQuest(questRowId & 0xFFFF, 1);
    }
}
