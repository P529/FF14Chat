using System.Collections.Generic;
using Dalamud.Game.Text;

namespace FF14Chat.Services;

public static class ChatTypes
{
    /// <summary>
    /// The high bits of a chat type carry source/target flags; the low
    /// 7 bits are the base kind, which is what filters care about.
    /// </summary>
    public static XivChatType Mask(XivChatType type) => (XivChatType)((ushort)type & 0x7F);

    /// <summary>
    /// Combat log kinds (damage, healing, buffs...). No tab shows these and
    /// they vastly outnumber chat, so they're dropped at capture.
    /// </summary>
    public static bool IsBattleSpam(XivChatType type) =>
        (ushort)Mask(type) is >= 41 and <= 55;

    /// <summary>
    /// Kinds whose text a player typed. Everything else — system, error, NPC
    /// dialogue, retainer sales, the battle log — is canned game output, and
    /// canned output is the same words every time in whatever language the
    /// client already runs in. StandardEmote is game-authored ("X bows."),
    /// CustomEmote is the player's own words, so only the latter qualifies.
    /// </summary>
    public static readonly XivChatType[] PlayerChat =
    [
        XivChatType.Say, XivChatType.Shout, XivChatType.Yell,
        XivChatType.TellIncoming, XivChatType.TellOutgoing,
        XivChatType.Party, XivChatType.CrossParty,
        XivChatType.Alliance, XivChatType.PvPTeam,
        XivChatType.FreeCompany, XivChatType.NoviceNetwork,
        XivChatType.CustomEmote,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2,
        XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
        XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];

    private static readonly HashSet<XivChatType> PlayerChatSet = [.. PlayerChat];

    /// <summary>Whether a player typed this kind's text; see <see cref="PlayerChat"/>.</summary>
    public static bool IsPlayerChat(XivChatType type) => PlayerChatSet.Contains(Mask(type));
}
