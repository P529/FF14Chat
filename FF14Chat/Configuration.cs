using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.Text;
using Newtonsoft.Json;

namespace FF14Chat;

public enum ChatTheme
{
    MutedGold = 0,
    RichGold = 1,
    ClassicBlue = 2,
    Ff7Remake = 3,
}

public enum TranslationProviderKind
{
    DeepL = 0,
    Anthropic = 1,
    OpenAiCompatible = 2,

    /// <summary>Google/Bing/Yandex free web endpoints, no account; see
    /// <see cref="Services.Translation.MachineTranslateProvider"/>.</summary>
    MachineTranslate = 3,
}

[Serializable]
public class TabConfig
{
    public string Name { get; set; } = "";

    // Replace: Newtonsoft otherwise APPENDS deserialized entries to
    // default-initialized collections, duplicating them on every load.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public HashSet<XivChatType> Channels { get; set; } = [];

    /// <summary>Also receives non-combat messages no other tab matched.</summary>
    public bool CatchAll { get; set; }

    /// <summary>Show an unread badge when messages arrive while unfocused.</summary>
    public bool NotifyUnread { get; set; }

    /// <summary>
    /// Chat command plain text is sent through in this tab (e.g. "/p").
    /// Null sends to the game's currently active channel.
    /// </summary>
    public string? SendCommand { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Replace: see TabConfig.Channels — appending duplicated every tab on load.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<TabConfig> Tabs { get; set; } = DefaultTabs();

    /// <summary>Window can no longer be moved or resized.</summary>
    public bool LockWindow { get; set; }

    /// <summary>Hide the vanilla chat log while this window is open.</summary>
    public bool HideVanillaChat { get; set; } = true;

    /// <summary>One-shot: window was initially placed over the vanilla chat.</summary>
    public bool PlacedAtVanillaChat { get; set; }

    /// <summary>Display order of tabs by tab id; unknown ids keep their position.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> TabOrder { get; set; } = [];

    /// <summary>Tell partners whose tabs were closed; not restored on load until they chat again.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> ClosedTellTabs { get; set; } = [];

    /// <summary>Chat font size in px; must be a native Axis size (10, 12, 14, 18).</summary>
    public int FontSize { get; set; } = 12;

    /// <summary>Merge the General and System tabs into a single "All" tab.</summary>
    public bool CombineGeneralSystem { get; set; }

    /// <summary>Online status dot on tell tabs (green online, red AFK, gray offline, blue unknown).</summary>
    public bool ShowTellPresence { get; set; } = true;

    /// <summary>Highlight log lines that mention the local player's name.</summary>
    public bool HighlightMentions { get; set; } = true;

    /// <summary>Color party/alliance sender names by their combat role.</summary>
    public bool RoleColorPartyNames { get; set; } = true;

    /// <summary>Job icon in front of party/alliance sender names.</summary>
    public bool JobIconPartyNames { get; set; } = true;

    /// <summary>Timestamps as 15:04 instead of 3:04 PM. Display only.</summary>
    public bool Use24HourClock { get; set; } = true;

    /// <summary>Collapse consecutive identical lines into one with a counter.</summary>
    public bool CollapseDuplicates { get; set; } = true;

    /// <summary>Render Discord-style ":shortcode:" emotes as Twemoji images.</summary>
    public bool RenderEmotes { get; set; } = true;

    /// <summary>
    /// While completions are open, Tab cycles the highlight and Space accepts,
    /// instead of Tab accepting the highlighted completion immediately.
    /// </summary>
    public bool TabCyclesSuggestions { get; set; }

    /// <summary>Game's own ItemDetail tooltip on item links instead of the custom card.</summary>
    public bool NativeItemTooltips { get; set; } = true;

    /// <summary>History retention: -1 forever, 0 wiped on every load, else days.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Per-channel color overrides, RGBA-packed (see ChatColors).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<XivChatType, uint> ChannelColors { get; set; } = [];

    /// <summary>Hide the window during cutscenes.</summary>
    public bool HideDuringCutscenes { get; set; } = true;

    /// <summary>Hide the window when the game UI is hidden (screenshot mode).</summary>
    public bool HideWhenUiHidden { get; set; } = true;

    /// <summary>Hide the window on loading screens.</summary>
    public bool HideInLoadingScreens { get; set; }

    /// <summary>Hide the window while in combat.</summary>
    public bool HideInBattle { get; set; }

    /// <summary>Legacy pre-v2 flag; superseded by <see cref="Theme"/>.</summary>
    public bool MutedTheme { get; set; } = true;

    /// <summary>Active theme, see <see cref="ChatTheme"/>.</summary>
    public int Theme { get; set; } = (int)ChatTheme.MutedGold;

    /// <summary>Window background opacity (0.3 – 1.0).</summary>
    public float BgOpacity { get; set; } = 0.78f;

    /// <summary>Translate arriving messages into <see cref="TargetLanguage"/>.</summary>
    public bool TranslateIncoming { get; set; }

    /// <summary>Offer translating typed input into <see cref="OutgoingLanguage"/> before sending.</summary>
    public bool TranslateOutgoing { get; set; }

    /// <summary>Active translation backend, see <see cref="TranslationProviderKind"/>.
    /// Defaults to the keyless one so translation works before any setup.</summary>
    public int TranslationProvider { get; set; } = (int)TranslationProviderKind.MachineTranslate;

    /// <summary>DeepL auth key; free-tier keys end in ":fx" and select the free host.</summary>
    public string DeepLApiKey { get; set; } = "";

    /// <summary>API key for the Anthropic / OpenAI-compatible backend.</summary>
    public string LlmApiKey { get; set; } = "";

    /// <summary>Model id passed to the LLM backend.</summary>
    public string LlmModel { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>Base URL of the OpenAI-compatible backend; "/chat/completions" is appended.</summary>
    public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>The outbound-data warning has been shown and accepted once; the
    /// keyless default means nothing else stands between a toggle and a request.</summary>
    public bool TranslationConsent { get; set; }

    /// <summary>When the chosen backend fails or is rate limited, translate through
    /// the free Google/Bing/Yandex endpoints instead of dropping the line.</summary>
    public bool FallbackToFree { get; set; } = true;

    /// <summary>Language incoming messages are translated into (DeepL target code).</summary>
    public string TargetLanguage { get; set; } = "EN-US";

    /// <summary>Language outgoing input is translated into (DeepL target code).</summary>
    public string OutgoingLanguage { get; set; } = "JA";

    // Replace: see TabConfig.Channels — appending duplicated every entry on load.
    /// <summary>Which player channels get translated; starts as all of them.
    /// System and NPC kinds are never eligible, so they are not in this set.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public HashSet<XivChatType> TranslateChannels { get; set; } = [.. Services.ChatTypes.PlayerChat];

    /// <summary>Translated-line color, RGBA-packed; 0 uses the built-in default.</summary>
    public uint TranslationColor { get; set; }

    /// <summary>Don't spend quota translating the local player's own lines.</summary>
    public bool SkipOwnMessages { get; set; } = true;

    /// <summary>Lines longer than this are never translated; guards against pasted walls of text.</summary>
    public int MaxTranslateChars { get; set; } = 300;

    /// <summary>Running total of characters sent to the translation API.</summary>
    public long TranslationCharsUsed { get; set; }

    /// <summary>Hovering a translated line shows the original and detected language.</summary>
    public bool ShowTranslationTooltip { get; set; } = true;

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
            Name = "Party",
            Channels =
            [
                XivChatType.Party, XivChatType.CrossParty,
                XivChatType.Alliance, XivChatType.PvPTeam,
            ],
            NotifyUnread = true,
            SendCommand = "/p",
        },
        new TabConfig
        {
            Name = "FC",
            Channels = [XivChatType.FreeCompany],
            NotifyUnread = true,
            SendCommand = "/fc",
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
