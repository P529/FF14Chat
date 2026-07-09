using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FF14Chat.Model;
using FF14Chat.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FF14Chat.Ui;

public class MainWindow : Window, IDisposable
{
    // Rendering is not virtualized yet, so cap how much we lay out per frame.
    private const int MaxRenderedMessages = 500;

    private const int MaxHistory = 100;

    private readonly Plugin plugin;
    private readonly TabManager tabs;
    private readonly Dictionary<string, string> drafts = [];

    private readonly List<string> sentHistory = [];
    private int historyPos = -1;
    private string historyStash = string.Empty;
    private bool focusInput;

    private readonly CommandIndex commandIndex = new();
    private List<CommandEntry> suggestions = [];
    private int suggestionIndex;
    private string suggestionQuery = string.Empty;

    private bool enterWasDown;
    private bool slashWasDown;
    private bool pendingSlash;
    private bool clearSelection;

    // Conditions in which Enter belongs to the game (advancing NPC dialogue,
    // cutscenes, occupied states), not to the chat window.
    private static readonly ConditionFlag[] EnterBlockedConditions =
    [
        ConditionFlag.Occupied,
        ConditionFlag.Occupied30,
        ConditionFlag.Occupied33,
        ConditionFlag.Occupied38,
        ConditionFlag.Occupied39,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.OccupiedSummoningBell,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
    ];

    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);

    public MainWindow(Plugin plugin, TabManager tabs) : base("FF14Chat###FF14ChatMain")
    {
        this.plugin = plugin;
        this.tabs = tabs;

        Plugin.Framework.Update += OnFrameworkUpdate;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(600, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    /// <summary>
    /// Runs before the game processes input for the tick, so consuming the
    /// key here keeps the vanilla chat box from opening. Render-time checks
    /// are too late: the game has already reacted to the key by then.
    /// </summary>
    private unsafe void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        var enterDown = Plugin.KeyState[VirtualKey.RETURN];
        var enterPressed = enterDown && !enterWasDown;
        enterWasDown = enterDown;

        var (slashKey, slashNeedsShift) = SlashKey();
        var slashDown = slashKey != VirtualKey.NO_KEY && Plugin.KeyState[slashKey];
        var slashPressed = slashDown && !slashWasDown;
        slashWasDown = slashDown;

        if (!IsOpen || (!enterPressed && !slashPressed))
            return;

        if (ImGui.GetIO().WantTextInput)
            return;

        var atkModule = RaptureAtkModule.Instance();
        if (atkModule != null && atkModule->AtkModule.IsTextInputActive())
            return;

        if (enterPressed)
        {
            foreach (var flag in EnterBlockedConditions)
            {
                if (Plugin.Condition[flag])
                    return;
            }

            Plugin.KeyState[VirtualKey.RETURN] = false;
            focusInput = true;
            return;
        }

        // Slash: only when the layout's shift requirement matches what's held.
        if (Plugin.KeyState[VirtualKey.SHIFT] != slashNeedsShift)
            return;

        Plugin.KeyState[slashKey] = false;
        focusInput = true;
        pendingSlash = true;
    }

    /// <summary>Resolves which physical key produces '/' on the current keyboard layout.</summary>
    private static (VirtualKey Key, bool NeedsShift) SlashKey()
    {
        var scan = VkKeyScanW('/');
        if (scan == -1)
            return (VirtualKey.NO_KEY, false);
        return ((VirtualKey)(scan & 0xFF), (scan & 0x100) != 0);
    }

    public override void Draw()
    {
        if (focusInput)
            ImGui.SetWindowFocus();

        using var tabBar = ImRaii.TabBar("##tabs", ImGuiTabBarFlags.Reorderable);
        if (!tabBar.Success)
            return;

        foreach (var tab in tabs.Snapshot())
        {
            var label = tab.Unread > 0
                ? $"{tab.Title} ({tab.Unread})###{tab.Id}"
                : $"{tab.Title}###{tab.Id}";

            if (tab.IsTell)
            {
                var open = true;
                using var item = ImRaii.TabItem(label, ref open);
                if (item.Success)
                    DrawTab(tab);
                if (!open)
                    tabs.Close(tab);
            }
            else
            {
                using var item = ImRaii.TabItem(label);
                if (item.Success)
                    DrawTab(tab);
            }
        }
    }

    private void DrawTab(TabState tab)
    {
        tabs.MarkRead(tab);

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2)))
        using (var child = ImRaii.Child("##log", new Vector2(-1, -ImGui.GetFrameHeightWithSpacing()), false))
        {
            if (child.Success)
                DrawLog(tab);
        }

        DrawInput(tab);
    }

    private void DrawLog(TabState tab)
    {
        var messages = tabs.MessagesSnapshot(tab);
        var pinnedToBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
        var newMessages = tab.Revision != tab.RenderedRevision;
        tab.RenderedRevision = tab.Revision;

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

    private void DrawInput(TabState tab)
    {
        drafts.TryGetValue(tab.Id, out var draft);
        draft ??= string.Empty;

        if (focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusInput = false;
            clearSelection = true;

            if (pendingSlash)
            {
                pendingSlash = false;
                if (draft.Length == 0)
                    draft = "/";
            }
        }

        var hint = tab.IsTell ? $"Message {tab.Title}…" : "Chat or /command…";
        var inputPos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint(
            $"##input{tab.Id}", hint, ref draft, 500,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory
            | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion,
            InputCallback);
        var inputActive = ImGui.IsItemActive();
        drafts[tab.Id] = draft;

        UpdateSuggestions(draft, inputActive || focusInput);
        if (suggestions.Count > 0)
            DrawSuggestions(tab, inputPos);

        if (!submitted)
            return;

        if (Submit(tab, draft))
            drafts[tab.Id] = string.Empty;

        // Keep typing without re-clicking the field.
        ImGui.SetKeyboardFocusHere(-1);
    }

    private void UpdateSuggestions(string draft, bool inputActive)
    {
        var wantSuggestions = inputActive
                              && draft.Length > 1
                              && draft[0] == '/'
                              && !draft.Contains(' ');
        if (!wantSuggestions)
        {
            suggestions = [];
            suggestionQuery = string.Empty;
            return;
        }

        if (draft != suggestionQuery)
        {
            suggestionQuery = draft;
            suggestions = commandIndex.Query(draft);
            suggestionIndex = 0;
        }
    }

    private void DrawSuggestions(TabState tab, Vector2 inputPos)
    {
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = suggestions.Count * lineHeight + ImGui.GetStyle().WindowPadding.Y * 2;
        var width = Math.Max(320f, ImGui.GetWindowWidth() * 0.6f);

        ImGui.SetNextWindowPos(new Vector2(inputPos.X, inputPos.Y - height - 4));
        ImGui.SetNextWindowSize(new Vector2(width, height));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;

        // Drawn as an overlay window so it can float above the input.
        if (!ImGui.Begin("##ff14chat-suggestions", flags))
        {
            ImGui.End();
            return;
        }

        for (var i = 0; i < suggestions.Count; i++)
        {
            var entry = suggestions[i];
            var selected = i == suggestionIndex;
            if (ImGui.Selectable($"{entry.Command}##sugg{i}", selected))
            {
                drafts[tab.Id] = entry.Command + " ";
                focusInput = true;
            }

            if (entry.Description.Length > 0)
            {
                ImGui.SameLine();
                using var dim = ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp);
                ImGui.TextUnformatted(entry.Description);
            }
        }

        ImGui.End();
    }

    private int InputCallback(ImGuiInputTextCallbackDataPtr data)
    {
        if (data.EventFlag == ImGuiInputTextFlags.CallbackAlways)
        {
            // Programmatic focus selects the whole buffer; typing would then
            // replace it. Put the cursor at the end with nothing selected.
            if (clearSelection)
            {
                clearSelection = false;
                data.CursorPos = data.BufTextLen;
                data.SelectionStart = data.BufTextLen;
                data.SelectionEnd = data.BufTextLen;
            }

            return 0;
        }

        // Tab: accept the highlighted suggestion.
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (suggestions.Count > 0)
            {
                var completed = suggestions[Math.Clamp(suggestionIndex, 0, suggestions.Count - 1)].Command + " ";
                data.DeleteChars(0, data.BufTextLen);
                data.InsertChars(0, completed);
            }

            return 0;
        }

        if (data.EventFlag != ImGuiInputTextFlags.CallbackHistory)
            return 0;

        // While suggestions are open, up/down moves the highlight, not history.
        if (suggestions.Count > 0)
        {
            if (data.EventKey == ImGuiKey.UpArrow)
                suggestionIndex = (suggestionIndex - 1 + suggestions.Count) % suggestions.Count;
            else if (data.EventKey == ImGuiKey.DownArrow)
                suggestionIndex = (suggestionIndex + 1) % suggestions.Count;
            return 0;
        }

        if (sentHistory.Count == 0)
            return 0;

        int newPos;
        if (data.EventKey == ImGuiKey.UpArrow)
        {
            if (historyPos == -1)
            {
                // Entering history: stash whatever is being typed.
                historyStash = System.Text.Encoding.UTF8.GetString(data.BufTextSpan);
                newPos = sentHistory.Count - 1;
            }
            else
            {
                newPos = Math.Max(0, historyPos - 1);
            }
        }
        else if (data.EventKey == ImGuiKey.DownArrow)
        {
            if (historyPos == -1)
                return 0;
            newPos = historyPos + 1 >= sentHistory.Count ? -1 : historyPos + 1;
        }
        else
        {
            return 0;
        }

        historyPos = newPos;
        var replacement = newPos == -1 ? historyStash : sentHistory[newPos];
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, replacement);
        return 0;
    }

    private bool Submit(TabState tab, string draft)
    {
        var text = draft.Trim();
        if (text.Length == 0)
            return true;

        var toSend = text[0] == '/'
            ? text
            : tab.IsTell
                ? $"/tell {tab.TellPartner} {text}"
                : text;

        if (!ChatSender.Send(toSend))
            return false;

        if (sentHistory.Count == 0 || sentHistory[^1] != text)
        {
            sentHistory.Add(text);
            if (sentHistory.Count > MaxHistory)
                sentHistory.RemoveAt(0);
        }

        historyPos = -1;
        historyStash = string.Empty;
        return true;
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
            ImGui.SetTooltip(segment.ItemHq ? $"{name} " : name);
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
            // Emote messages already contain the player's name in the text.
            XivChatType.CustomEmote or XivChatType.StandardEmote => string.Empty,
            XivChatType.FreeCompany => $"[FC]<{message.Sender}>",
            _ => $"{message.Sender}:",
        };
    }
}
