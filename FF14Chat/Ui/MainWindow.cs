using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FF14Chat.Model;
using FF14Chat.Services;

namespace FF14Chat.Ui;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly MessageStore store;

    private long lastRenderedRevision = -1;

    public MainWindow(Plugin plugin, MessageStore store) : base("FF14Chat###FF14ChatMain")
    {
        this.plugin = plugin;
        this.store = store;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(600, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Dispose() { }

    public override void Draw()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2));
        using var child = ImRaii.Child("##log", new Vector2(-1, -1), false);
        if (!child.Success)
            return;

        var messages = store.Snapshot();
        var pinnedToBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
        var newMessages = store.Revision != lastRenderedRevision;
        lastRenderedRevision = store.Revision;

        if (messages.Length == 0)
        {
            using var dim = ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp);
            ImGui.TextWrapped("No messages yet — chat will appear here as it happens.");
            return;
        }

        foreach (var message in messages)
            DrawMessage(message);

        if (pinnedToBottom && newMessages)
            ImGui.SetScrollHereY(1f);
    }

    private static void DrawMessage(Message message)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted($"[{message.Timestamp:HH:mm}]");
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.For(message.Type)))
        {
            ImGui.TextWrapped(FormatLine(message));
        }
    }

    private static string FormatLine(Message message)
    {
        if (message.Sender.Length == 0)
            return message.Text;

        return message.Type switch
        {
            XivChatType.TellIncoming => $"{message.Sender} >> {message.Text}",
            XivChatType.TellOutgoing => $">> {message.Sender}: {message.Text}",
            XivChatType.Party or XivChatType.CrossParty => $"({message.Sender}) {message.Text}",
            XivChatType.Alliance => $"(({message.Sender})) {message.Text}",
            XivChatType.CustomEmote or XivChatType.StandardEmote => $"{message.Sender}{message.Text}",
            XivChatType.FreeCompany => $"[FC]<{message.Sender}> {message.Text}",
            _ => $"{message.Sender}: {message.Text}",
        };
    }
}
