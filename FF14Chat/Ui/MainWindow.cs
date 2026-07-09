using System;
using System.Collections.Generic;
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
    // Rendering is not virtualized yet, so cap how much we lay out per frame.
    private const int MaxRenderedMessages = 500;

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

        var first = Math.Max(0, messages.Length - MaxRenderedMessages);
        for (var i = first; i < messages.Length; i++)
            DrawMessage(messages[i]);

        if (pinnedToBottom && newMessages)
            ImGui.SetScrollHereY(1f);
    }

    private static void DrawMessage(Message message)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted($"[{message.Timestamp:HH:mm}]");
        }

        var channelColor = ChatColors.For(message.Type);

        var prefix = FormatPrefix(message);
        if (prefix.Length > 0)
            DrawSegmentText(prefix + " ", channelColor, null);

        if (message.Segments.Count > 0)
        {
            foreach (var segment in message.Segments)
                DrawSegmentText(segment.Text, segment.Color ?? channelColor, segment);
        }
        else
        {
            DrawSegmentText(message.Text, channelColor, null);
        }
    }

    /// <summary>
    /// Draws text continuing the current chat line, word-wrapping against the
    /// window edge. Assumes the previous ImGui item is the preceding chunk of
    /// this same line (the timestamp starts every line).
    /// </summary>
    private static void DrawSegmentText(string text, Vector4 color, MessageSegment? segment)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var lines = text.Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var forceNewLine = li > 0;
            foreach (var token in Tokenize(lines[li]))
            {
                DrawToken(token, segment, forceNewLine);
                forceNewLine = false;
            }
        }
    }

    private static void DrawToken(string token, MessageSegment? segment, bool forceNewLine)
    {
        if (!forceNewLine)
        {
            var tokenWidth = ImGui.CalcTextSize(token).X;
            var lastEnd = ImGui.GetItemRectMax().X;
            var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            if (lastEnd + tokenWidth <= rightEdge)
                ImGui.SameLine(0, 0);
        }

        ImGui.TextUnformatted(token);

        if (segment is { ItemId: not null } && ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var name = segment.ItemName ?? $"Item #{segment.ItemId}";
            ImGui.SetTooltip(segment.ItemHq ? $"{name} " : name);
        }
    }

    /// <summary>Splits a line into words, each keeping its trailing spaces.</summary>
    private static IEnumerable<string> Tokenize(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var space = line.IndexOf(' ', i);
            if (space < 0)
            {
                yield return line[i..];
                break;
            }

            var end = space;
            while (end < line.Length && line[end] == ' ')
                end++;
            yield return line[i..end];
            i = end;
        }
    }

    private static string FormatPrefix(Message message)
    {
        if (message.Sender.Length == 0)
            return string.Empty;

        return message.Type switch
        {
            XivChatType.TellIncoming => $"{message.Sender} >>",
            XivChatType.TellOutgoing => $">> {message.Sender}:",
            XivChatType.Party or XivChatType.CrossParty => $"({message.Sender})",
            XivChatType.Alliance => $"(({message.Sender}))",
            XivChatType.CustomEmote or XivChatType.StandardEmote => message.Sender,
            XivChatType.FreeCompany => $"[FC]<{message.Sender}>",
            _ => $"{message.Sender}:",
        };
    }
}
