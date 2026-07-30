using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Text;

namespace FF14Chat.Ui;

/// <summary>Approximations of the game's default log colors per channel.</summary>
public static class ChatColors
{
    public static readonly Vector4 Timestamp = new(0.55f, 0.55f, 0.55f, 1f);

    /// <summary>Item/map link text without an explicit color from the game.</summary>
    public static readonly Vector4 Link = new(0.45f, 0.70f, 1.00f, 1f);

    private static Dictionary<XivChatType, Vector4> overrides = [];

    /// <summary>Installs the user's per-channel colors (RGBA-packed, from config).</summary>
    public static void SetOverrides(Dictionary<XivChatType, uint> stored)
    {
        var table = new Dictionary<XivChatType, Vector4>(stored.Count);
        foreach (var (type, rgba) in stored)
            table[type] = Services.PackedColor.Unpack(rgba);

        overrides = table;
    }

    public static Vector4 For(XivChatType type) =>
        overrides.TryGetValue(type, out var custom) ? custom : Default(type);

    public static Vector4 Default(XivChatType type) => type switch
    {
        XivChatType.Say => new Vector4(0.97f, 0.97f, 0.97f, 1f),
        XivChatType.Shout => new Vector4(1.00f, 0.73f, 0.44f, 1f),
        XivChatType.Yell => new Vector4(1.00f, 1.00f, 0.35f, 1f),
        XivChatType.TellIncoming => new Vector4(1.00f, 0.71f, 0.85f, 1f),
        XivChatType.TellOutgoing => new Vector4(1.00f, 0.71f, 0.85f, 1f),
        XivChatType.Party => new Vector4(0.40f, 0.75f, 0.94f, 1f),
        XivChatType.CrossParty => new Vector4(0.40f, 0.75f, 0.94f, 1f),
        XivChatType.Alliance => new Vector4(1.00f, 0.62f, 0.25f, 1f),
        XivChatType.FreeCompany => new Vector4(0.67f, 0.85f, 0.90f, 1f),
        XivChatType.NoviceNetwork => new Vector4(0.83f, 0.98f, 0.78f, 1f),
        XivChatType.CustomEmote => new Vector4(0.72f, 0.94f, 0.86f, 1f),
        XivChatType.StandardEmote => new Vector4(0.72f, 0.94f, 0.86f, 1f),
        XivChatType.Echo => new Vector4(0.75f, 0.75f, 0.75f, 1f),
        XivChatType.SystemMessage => new Vector4(0.80f, 0.80f, 0.80f, 1f),
        XivChatType.SystemError => new Vector4(1.00f, 0.45f, 0.45f, 1f),
        XivChatType.ErrorMessage => new Vector4(1.00f, 0.45f, 0.45f, 1f),
        XivChatType.GatheringSystemMessage => new Vector4(0.80f, 0.80f, 0.80f, 1f),
        XivChatType.RetainerSale => new Vector4(0.80f, 0.80f, 0.80f, 1f),
        XivChatType.Notice => new Vector4(0.90f, 0.85f, 0.60f, 1f),
        XivChatType.Urgent => new Vector4(1.00f, 0.55f, 0.55f, 1f),
        XivChatType.NPCDialogue => new Vector4(0.90f, 0.90f, 0.90f, 1f),
        XivChatType.NPCDialogueAnnouncements => new Vector4(0.90f, 0.90f, 0.90f, 1f),
        XivChatType.Ls1 or XivChatType.Ls2 or XivChatType.Ls3 or XivChatType.Ls4
            or XivChatType.Ls5 or XivChatType.Ls6 or XivChatType.Ls7 or XivChatType.Ls8
            => new Vector4(0.85f, 1.00f, 0.60f, 1f),
        XivChatType.CrossLinkShell1 or XivChatType.CrossLinkShell2 or XivChatType.CrossLinkShell3
            or XivChatType.CrossLinkShell4 or XivChatType.CrossLinkShell5 or XivChatType.CrossLinkShell6
            or XivChatType.CrossLinkShell7 or XivChatType.CrossLinkShell8
            => new Vector4(0.85f, 1.00f, 0.60f, 1f),
        XivChatType.PvPTeam => new Vector4(0.90f, 0.70f, 0.90f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };
}
