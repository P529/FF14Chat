using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FF14Chat.Services;
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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/ff14chat";

    private const int HydrateLimit = 1000;

    public Configuration Configuration { get; init; }
    public MessageStore MessageStore { get; init; }
    public TabManager TabManager { get; init; }
    public MessageDatabase Database { get; init; }

    public readonly WindowSystem WindowSystem = new("FF14Chat");
    private ChatCapture ChatCapture { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        MessageStore = new MessageStore();
        TabManager = new TabManager(Configuration);

        Database = new MessageDatabase(
            System.IO.Path.Combine(PluginInterface.GetPluginConfigDirectory(), "chat.db"));
        HydrateFromDatabase();

        ChatCapture = new ChatCapture(MessageStore, TabManager, Database);

        MainWindow = new MainWindow(this, TabManager);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the FF14Chat window.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        ChatCapture.Dispose();
        Database.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void HydrateFromDatabase()
    {
        try
        {
            foreach (var message in Database.LoadRecent(HydrateLimit))
            {
                MessageStore.Add(message);
                TabManager.Route(message);
            }
        }
        catch (System.Exception e)
        {
            Log.Error(e, "Failed to load chat history");
        }

        // Restored history is not new.
        foreach (var tab in TabManager.Snapshot())
            TabManager.MarkRead(tab);
    }

    private void OnCommand(string command, string args) => MainWindow.Toggle();

    public void ToggleMainUi() => MainWindow.Toggle();
}
