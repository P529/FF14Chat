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
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/ff14chat";

    /// <summary>
    /// The game has no examine command, only the target context menu, so this
    /// one is ours. Registered with the game's command manager rather than
    /// handled inside our input box so it also works from the native chat box
    /// and from macros.
    /// </summary>
    internal const string ExamineCommand = "/examine";

    /// <summary>
    /// Reads the mount off a nearby character. Registered with the game's
    /// command manager for the same reason /examine is: it then works from the
    /// native chat box and from macros too. Not "/mount" — that one is the
    /// game's own summon command, and a Dalamud handler would shadow it.
    /// </summary>
    internal const string MountCommand = "/mountid";

    private const string ExamineHelp =
        "Examine a nearby player: /examine Name@World (no argument examines your target).";

    private const string MountHelp =
        "Show what mount a character is riding: /mountid Name@World (no argument uses your target).";

    /// <summary>False when another plugin already owns the name, or when the
    /// tweak is switched off — either way, don't unregister someone else's.</summary>
    private bool examineRegistered;

    private bool mountRegistered;

    private readonly bool commandRegistered;

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
    private GameContextMenu GameContextMenu { get; init; }
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
                // No General tab (renamed or deleted on an older config) means
                // "after General" has no meaning; append rather than let a -1
                // put the new tab at the very front.
                var general = Configuration.Tabs.FindIndex(t => t.Name == "General");
                var index = general >= 0 ? general + 1 : Configuration.Tabs.Count;
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

        // v6: unread badges were opt-in and only the stock Party and FC tabs
        // ever set the flag, so every other tab — including any the user made
        // themselves — stayed silent no matter what arrived in it. Badges are
        // the default now; General and System keep quiet because they catch
        // everything and would never be unlit.
        if (Configuration.Version < 6)
        {
            foreach (var tab in Configuration.Tabs)
                tab.NotifyUnread = tab.Name is not ("General" or "System");

            Configuration.Version = 6;
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

        // Everything past this point either subscribes to a game event or
        // claims a command name. Dalamud never calls Dispose on a constructor
        // that threw, so a failure here would otherwise leave those callbacks
        // firing forever against a half-built plugin: tear down what was
        // already wired before letting the exception out.
        try
        {
            Translation.Changed += OnTranslationChanged;
            Framework.Update += OnFrameworkUpdate;

            ChatCapture = new ChatCapture(MessageStore, TabManager, Database, Presence, Translation);

            MainWindow = new MainWindow(this, TabManager);
            SettingsWindow = new SettingsWindow(this, MainWindow);
            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(SettingsWindow);

            commandRegistered = CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the FF14Chat window.",
            });

            ApplyCommandTweaks();

            GameContextMenu = new GameContextMenu(Configuration);

            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Brings the optional commands in line with their settings toggles. Safe
    /// to call at any time: it only adds what isn't ours yet and only removes
    /// what is.
    /// </summary>
    internal void ApplyCommandTweaks()
    {
        SetCommand(Configuration.ExamineCommandEnabled, ExamineCommand, ExamineHelp, OnExamine, ref examineRegistered);
        SetCommand(Configuration.MountIdCommandEnabled, MountCommand, MountHelp, OnMount, ref mountRegistered);
    }

    private static void SetCommand(
        bool wanted, string name, string help, IReadOnlyCommandInfo.HandlerDelegate handler, ref bool registered)
    {
        if (wanted == registered)
            return;

        if (!wanted)
        {
            CommandManager.RemoveHandler(name);
            registered = false;
            return;
        }

        registered = CommandManager.AddHandler(name, new CommandInfo(handler) { HelpMessage = help });
        if (!registered)
            Log.Warning("{Command} is already taken by another plugin; the chat completion will not work", name);
    }

    /// <summary>
    /// Also runs on a failed construction, so every member here must tolerate
    /// being null and every unregistration must be one this plugin actually
    /// made — removing a command handler we never installed would unregister
    /// whichever plugin does own that name.
    /// </summary>
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        Framework.Update -= OnFrameworkUpdate;
        if (Translation != null)
            Translation.Changed -= OnTranslationChanged;

        WindowSystem.RemoveAllWindows();
        SettingsWindow?.Dispose();
        MainWindow?.Dispose();
        ChatCapture?.Dispose();
        GameContextMenu?.Dispose();
        Translation?.Dispose();
        Presence?.Dispose();
        Database?.Dispose();
        Emotes.Dispose();

        if (commandRegistered)
            CommandManager.RemoveHandler(CommandName);
        if (examineRegistered)
            CommandManager.RemoveHandler(ExamineCommand);
        if (mountRegistered)
            CommandManager.RemoveHandler(MountCommand);
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

    /// <summary>
    /// "/examine Name@World" on a nearby player, or "/examine" on the current
    /// target. Failures print like the game's own command errors instead of
    /// raising a toast: the user is looking at the chat log already.
    /// </summary>
    private void OnExamine(string command, string args)
    {
        var partner = args.Trim();
        if (partner.Length == 0)
        {
            if (TargetManager.Target is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter targeted)
                PlayerActions.Examine(targeted);
            else
                ChatGui.PrintError("No player targeted. Usage: /examine Name@World");

            return;
        }

        if (!PlayerActions.ExamineByName(partner))
            ChatGui.PrintError($"Unable to examine {PlayerActions.Split(partner).Name}: they must be nearby.");
    }

    /// <summary>
    /// "/mountid Name@World", or "/mountid" for the current target. Prints the
    /// mount as a clickable item link where one exists — MessageParser already
    /// turns item payloads into links, so it lands in our own window with the
    /// tooltip and context menu for free.
    /// </summary>
    private void OnMount(string command, string args)
    {
        var query = args.Trim();

        // ICharacter rather than IPlayerCharacter on the no-argument path, so
        // a mounted NPC works too.
        var character = query.Length == 0
            ? TargetManager.Target as Dalamud.Game.ClientState.Objects.Types.ICharacter
            : PlayerActions.FindNearby(query);

        if (character == null)
        {
            ChatGui.PrintError(query.Length == 0
                ? "No character targeted. Usage: /mountid Name@World"
                : $"Unable to check {PlayerActions.Split(query).Name}: they must be nearby.");
            return;
        }

        var name = character.Name.TextValue;
        var mountId = MountActions.MountId(character);
        if (mountId == 0)
        {
            ChatGui.Print($"{name} is not mounted.", "FF14Chat");
            return;
        }

        var message = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
            .AddText($"{name} is riding ");

        var itemId = MountActions.TeachingItemId(mountId);
        if (itemId != 0)
        {
            // The item name is always properly cased, and it is the thing
            // being linked anyway.
            message.AddItemLink(itemId, false);
        }
        else
        {
            message.AddText(MountActions.MountName(mountId) ?? $"mount #{mountId}");
        }

        ChatGui.Print(message.AddText(".").Build(), "FF14Chat");
    }

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleConfigUi() => SettingsWindow.Toggle();
}
