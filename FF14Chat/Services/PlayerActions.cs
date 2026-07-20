using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FF14Chat.Services;

/// <summary>
/// Native actions on a player identified by "Name@World". Actions that need
/// an entity (target, examine, plate) only work while the player is nearby;
/// the party invite goes by name + world id like the vanilla social UI.
/// </summary>
public static class PlayerActions
{
    public static (string Name, string World) Split(string partner)
    {
        var at = partner.IndexOf('@');
        return at > 0 ? (partner[..at], partner[(at + 1)..]) : (partner, string.Empty);
    }

    /// <summary>The player's character in the object table, if in range.</summary>
    public static IPlayerCharacter? FindNearby(string partner)
    {
        var (name, world) = Split(partner);
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || player.Name.TextValue != name)
                continue;

            if (world.Length == 0
                || player.HomeWorld.ValueNullable?.Name.ExtractText() == world)
            {
                return player;
            }
        }

        return null;
    }

    public static void Target(IPlayerCharacter player) => Plugin.TargetManager.Target = player;

    public static unsafe void Examine(IPlayerCharacter player)
    {
        var agent = AgentInspect.Instance();
        if (agent != null)
            agent->ExamineCharacter(player.EntityId);
    }

    public static unsafe void OpenAdventurerPlate(IPlayerCharacter player)
    {
        var agent = AgentCharaCard.Instance();
        if (agent != null)
            agent->OpenCharaCard((GameObject*)player.Address);
    }

    public static unsafe bool InviteToParty(string partner)
    {
        var (name, world) = Split(partner);
        var infoModule = InfoModule.Instance();
        if (name.Length == 0 || infoModule == null)
            return false;

        var proxy = (InfoProxyPartyInvite*)infoModule->GetInfoProxyById(InfoProxyId.PartyInvite);
        if (proxy == null)
            return false;

        var worldId = GameData.WorldIdByName(world);
        var nameUtf8 = Utf8String.FromString(name);
        try
        {
            return proxy->InviteToParty(0, nameUtf8->StringPtr, worldId);
        }
        finally
        {
            nameUtf8->Dtor(true);
        }
    }
}
