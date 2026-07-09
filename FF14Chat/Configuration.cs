using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.Text;

namespace FF14Chat;

[Serializable]
public class TabConfig
{
    public string Name { get; set; } = "";
    public HashSet<XivChatType> Channels { get; set; } = [];

    /// <summary>Also receives non-combat messages no other tab matched.</summary>
    public bool CatchAll { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public List<TabConfig> Tabs { get; set; } = DefaultTabs();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    private static List<TabConfig> DefaultTabs() =>
    [
        new TabConfig
        {
            Name = "General",
            Channels =
            [
                XivChatType.Say, XivChatType.Shout, XivChatType.Yell,
                XivChatType.TellIncoming, XivChatType.TellOutgoing,
                XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance,
                XivChatType.NoviceNetwork,
                XivChatType.CustomEmote, XivChatType.StandardEmote,
                XivChatType.Echo, XivChatType.PvPTeam,
                XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
                XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
                XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2,
                XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
                XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
                XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
            ],
        },
        new TabConfig
        {
            Name = "FC",
            Channels = [XivChatType.FreeCompany],
        },
        new TabConfig
        {
            Name = "System",
            CatchAll = true,
            Channels =
            [
                XivChatType.SystemMessage, XivChatType.SystemError,
                XivChatType.ErrorMessage, XivChatType.GatheringSystemMessage,
                XivChatType.Echo, XivChatType.Notice, XivChatType.Urgent,
                XivChatType.RetainerSale, XivChatType.NPCDialogue,
                XivChatType.NPCDialogueAnnouncements,
            ],
        },
    ];
}
