using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.Text;

namespace FF14Chat;

public enum ChatTheme
{
    MutedGold = 0,
    RichGold = 1,
    ClassicBlue = 2,
    Ff7Remake = 3,
}

[Serializable]
public class TabConfig
{
    public string Name { get; set; } = "";
    public HashSet<XivChatType> Channels { get; set; } = [];

    /// <summary>Also receives non-combat messages no other tab matched.</summary>
    public bool CatchAll { get; set; }

    /// <summary>Show an unread badge when messages arrive while unfocused.</summary>
    public bool NotifyUnread { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public List<TabConfig> Tabs { get; set; } = DefaultTabs();

    /// <summary>Window can no longer be moved or resized.</summary>
    public bool LockWindow { get; set; }

    /// <summary>Hide the vanilla chat log while this window is open.</summary>
    public bool HideVanillaChat { get; set; } = true;

    /// <summary>One-shot: window was initially placed over the vanilla chat.</summary>
    public bool PlacedAtVanillaChat { get; set; }

    /// <summary>Display order of tabs by tab id; unknown ids keep their position.</summary>
    public List<string> TabOrder { get; set; } = [];

    /// <summary>Chat font size in px; must be a native Axis size (10, 12, 14, 18).</summary>
    public int FontSize { get; set; } = 12;

    /// <summary>Legacy pre-v2 flag; superseded by <see cref="Theme"/>.</summary>
    public bool MutedTheme { get; set; } = true;

    /// <summary>Active theme, see <see cref="ChatTheme"/>.</summary>
    public int Theme { get; set; } = (int)ChatTheme.MutedGold;

    /// <summary>Window background opacity (0.3 – 1.0).</summary>
    public float BgOpacity { get; set; } = 0.78f;

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
            NotifyUnread = true,
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
