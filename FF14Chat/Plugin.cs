using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FF14Chat.Services;
using FF14Chat.Services.Translation;
using FF14Chat.Ui;

namespace FF14Chat;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static Dalamud.Plugin.Services.ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/ff14chat";

    private const int HydrateRecentLimit = 5000;
    private const int HydrateTellLimit = 2000;

    public Configuration Configuration { get; init; }
    public MessageStore MessageStore { get; init; }
    public TabManager TabManager { get; init; }
    public MessageDatabase Database { get; init; }
    public PresenceTracker Presence { get; init; }
    public TranslationService Translation { get; init; }

    public readonly WindowSystem WindowSystem = new("FF14Chat");
    private ChatCapture ChatCapture { get; init; }
    private MainWindow MainWindow { get; init; }
    private SettingsWindow SettingsWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // v1: unread badges became opt-in per tab; older saved configs need
        // the FC tab flagged (tell tabs always track).
        if (Configuration.Version < 1)
        {
            foreach (var tab in Configuration.Tabs)
            {
                if (tab.Name == "FC")
                    tab.NotifyUnread = true;
            }

            Configuration.Version = 1;
            Configuration.Save();
        }

        // v2: the muted flag became a theme choice.
        if (Configuration.Version < 2)
        {
            Configuration.Theme = (int)(Configuration.MutedTheme ? ChatTheme.MutedGold : ChatTheme.RichGold);
            Configuration.Version = 2;
            Configuration.Save();
        }

        // v3: a dedicated Party tab joined the defaults; older configs get it
        // inserted after General unless they already have a party tab.
        if (Configuration.Version < 3)
        {
            if (!Configuration.Tabs.Exists(t => t.Channels.Contains(Dalamud.Game.Text.XivChatType.Party) && !t.CatchAll && t.Name != "General"))
            {
                var index = Configuration.Tabs.FindIndex(t => t.Name == "General") + 1;
                Configuration.Tabs.Insert(index, new TabConfig
                {
                    Name = "Party",
                    Channels =
                    [
                        Dalamud.Game.Text.XivChatType.Party, Dalamud.Game.Text.XivChatType.CrossParty,
                        Dalamud.Game.Text.XivChatType.Alliance, Dalamud.Game.Text.XivChatType.PvPTeam,
                    ],
                    NotifyUnread = true,
                });
            }

            Configuration.Version = 3;
            Configuration.Save();
        }

        // v4: earlier versions appended deserialized collections onto the
        // defaults (Newtonsoft population), duplicating tabs on every load.
        if (Configuration.Version < 4)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            var deduped = new System.Collections.Generic.List<TabConfig>();
            // Keep the LAST occurrence per name: appended entries are the
            // user's saved config, the earlier ones are stale defaults.
            for (var i = Configuration.Tabs.Count - 1; i >= 0; i--)
            {
                if (seen.Add(Configuration.Tabs[i].Name))
                    deduped.Insert(0, Configuration.Tabs[i]);
            }

            Configuration.Tabs = deduped;

            var orderSeen = new System.Collections.Generic.HashSet<string>();
            Configuration.TabOrder.RemoveAll(id => !orderSeen.Add(id));
            var closedSeen = new System.Collections.Generic.HashSet<string>();
            Configuration.ClosedTellTabs.RemoveAll(id => !closedSeen.Add(id));

            Configuration.Version = 4;
            Configuration.Save();
        }

        // v5: tabs gained a send channel; wire up the stock Party/FC tabs.
        if (Configuration.Version < 5)
        {
            foreach (var tab in Configuration.Tabs)
            {
                tab.SendCommand ??= tab.Name switch
                {
                    "Party" => "/p",
                    "FC" => "/fc",
                    _ => null,
                };
            }

            Configuration.Version = 5;
            Configuration.Save();
        }

        Ui.ChatColors.SetOverrides(Configuration.ChannelColors);

        MessageStore = new MessageStore();
        TabManager = new TabManager(Configuration, MessageStore);

        Database = new MessageDatabase(
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "chat.db"),
            Configuration.RetentionDays);
        PruneStaleConfigIds();
        HydrateFromDatabase();

        Presence = new PresenceTracker(TabManager);

        // Constructed after hydration so restoring hundreds of stored lines
        // cannot fire hundreds of API calls on login.
        Translation = new TranslationService(Configuration);
        Translation.Changed += OnTranslationChanged;
        Framework.Update += OnFrameworkUpdate;

        ChatCapture = new ChatCapture(MessageStore, TabManager, Database, Presence, Translation);

        MainWindow = new MainWindow(this, TabManager);
        SettingsWindow = new SettingsWindow(this, MainWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(SettingsWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the FF14Chat window.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        Framework.Update -= OnFrameworkUpdate;
        Translation.Changed -= OnTranslationChanged;

        WindowSystem.RemoveAllWindows();
        SettingsWindow.Dispose();
        MainWindow.Dispose();
        ChatCapture.Dispose();
        Translation.Dispose();
        Presence.Dispose();
        Database.Dispose();
        Emotes.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>
    /// Drops config ids that can no longer matter: closed-tell flags for
    /// partners with no surviving history (hydration only spawns tabs from
    /// rows, so the flag protects nothing), and order slots for deleted
    /// fixed tabs or tells that are neither closed nor on disk. Runs after
    /// the database's retention prune so "surviving" is accurate.
    /// </summary>
    private void PruneStaleConfigIds()
    {
        try
        {
            var onDisk = Database.TellPartnersOnDisk();
            var removed = Configuration.ClosedTellTabs.RemoveAll(p => !onDisk.Contains(p));

            var fixedIds = new System.Collections.Generic.HashSet<string>(
                Configuration.Tabs.ConvertAll(t => "tab:" + t.Name))
            {
                // The combined General+System tab reuses this id even when no
                // tab literally named General exists.
                "tab:General",
            };
            removed += Configuration.TabOrder.RemoveAll(id => id.StartsWith("tab:", System.StringComparison.Ordinal)
                ? !fixedIds.Contains(id)
                : id.StartsWith("tell:", System.StringComparison.Ordinal)
                  && !onDisk.Contains(id["tell:".Length..])
                  && !Configuration.ClosedTellTabs.Contains(id["tell:".Length..]));

            if (removed > 0)
                Configuration.Save();
        }
        catch (System.Exception e)
        {
            Log.Warning(e, "Stale config id prune failed");
        }
    }

    private void HydrateFromDatabase()
    {
        try
        {
            foreach (var message in Database.LoadForHydration(HydrateRecentLimit, HydrateTellLimit))
            {
                MessageStore.Add(message);
                TabManager.Route(message, live: false);
            }
        }
        catch (System.Exception e)
        {
            Log.Error(e, "Failed to load chat history");
        }

        // Restored history is not new, and restored tell tabs should sit
        // where the user left them.
        foreach (var tab in TabManager.Snapshot())
            TabManager.MarkRead(tab);
        TabManager.ApplySavedOrder();
    }

    // Set on a translation worker thread, consumed on the framework thread
    // (which is also the draw thread) so the renderer state below is only ever
    // touched from where it belongs.
    private volatile bool translationsChanged;

    private void OnTranslationChanged() => translationsChanged = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!translationsChanged)
            return;

        translationsChanged = false;

        // A resolved translation changes what an already-rendered message
        // draws but adds no message, so no tab revision moved and the log
        // would keep its stale layout. Rewinding RenderedRevision makes the
        // next frame count as new content — which is what re-pins a
        // bottom-pinned view as the extra line appears — without bumping
        // Revision and needlessly rebuilding every tab's snapshot. -1 is the
        // never-drawn sentinel and would force a pin, so stop above it.
        foreach (var tab in TabManager.Snapshot())
        {
            if (tab.RenderedRevision > 0)
                tab.RenderedRevision--;
        }
    }

    private void OnCommand(string command, string args) => MainWindow.Toggle();

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleConfigUi() => SettingsWindow.Toggle();
}
