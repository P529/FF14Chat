using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using FF14Chat.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace FF14Chat.Ui;

/// <summary>
/// The input row: the text field and its callback, autocomplete for
/// commands, emotes and player names, sent-message history, the keybind
/// entry points the game hands over to, and the send path including the
/// outgoing-translation detour.
/// </summary>
public partial class MainWindow
{
    private bool inputActiveLastFrame;
    private TabState? inputTab;

    // Say -> Party -> Alliance -> FC, the useful everyday rotation.
    private static readonly int[] ChannelCycle = [1, 2, 3, 6];

    /// <summary>Switches the game's active input channel to the next/previous in the cycle.</summary>
    private unsafe void CycleGameChannel(int direction)
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return;

        var index = Array.IndexOf(ChannelCycle, (int)agent->CurrentChannel);
        var next = ChannelCycle[(index + direction + ChannelCycle.Length) % ChannelCycle.Length];
        GameKeybindChannel(next, 0);
    }

    internal void GameKeybindFocus() => focusInput = true;

    internal void GameKeybindSlash()
    {
        pendingSlash = true;
        focusInput = true;
    }

    /// <summary>Reply: open the tell tab of the most recent tell in either direction.</summary>
    internal void GameKeybindReply()
    {
        var messages = plugin.MessageStore.Snapshot();
        for (var i = messages.Length - 1; i >= 0; i--)
        {
            if (messages[i].TellPartner is { Length: > 0 } partner)
            {
                OpenTellTabFor(partner);
                return;
            }
        }
    }

    /// <summary>Switches the game's active input channel, nothing else.</summary>
    private static unsafe void ChangeGameChannel(int channel, uint linkshell)
    {
        var shell = RaptureShellModule.Instance();
        if (shell == null)
            return;

        var empty = Utf8String.FromString(string.Empty);
        try
        {
            shell->ChangeChatChannel(channel, linkshell, empty, true);
        }
        finally
        {
            empty->Dtor(true);
        }
    }

    internal void GameKeybindChannel(int channel, uint linkshell)
    {
        ChangeGameChannel(channel, linkshell);

        // The channel's dedicated tab (Party, FC, …) is always selected when
        // one exists. Channels without one (yell, linkshells, …) fall back
        // to the first tab following the game's active channel (General),
        // but only when the current tab can't send there.
        var commands = ChannelSendCommands(channel);
        var snapshot = tabs.Snapshot();
        var target = Array.Find(snapshot, t => !t.IsTell && MatchesCommand(t.SendCommand, commands));
        if (target == null && inputTab is not { IsTell: false, SendCommand: null })
            target = Array.Find(snapshot, t => !t.IsTell && t.SendCommand == null);

        if (target != null)
            selectTabId = target.Id;

        focusInput = true;

        static bool MatchesCommand(string? sendCommand, string[]? commands)
        {
            if (sendCommand == null || commands == null)
                return false;

            foreach (var command in commands)
            {
                if (sendCommand.Equals(command, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    /// <summary>Send commands equivalent to a RaptureShell channel number, null if none.</summary>
    private static string[]? ChannelSendCommands(int channel) => channel switch
    {
        1 => ["/s", "/say"],
        2 => ["/p", "/party"],
        3 => ["/a", "/alliance"],
        4 => ["/y", "/yell"],
        5 => ["/sh", "/shout"],
        6 => ["/fc", "/freecompany"],
        8 => ["/n", "/novice"],
        _ => null,
    };

    /// <summary>Numbered channel families: long form, short form, channel of #1.</summary>
    private static readonly (string Long, string Short, int FirstChannel)[] LinkshellForms =
    [
        ("/cwlinkshell", "/cwl", 9),
        ("/linkshell", "/l", 19),
    ];

    /// <summary>
    /// The input channel a leading command switches to, null when the command
    /// isn't a channel prefix. The inverse of <see cref="ChannelSendCommands"/>,
    /// plus the linkshells — those have no dedicated tab, so they aren't in it.
    /// </summary>
    private static (int Channel, uint Linkshell)? ChannelForCommand(string command)
    {
        var fixedChannel = command switch
        {
            "/s" or "/say" => 1,
            "/p" or "/party" => 2,
            "/a" or "/alliance" => 3,
            "/y" or "/yell" => 4,
            "/sh" or "/shout" => 5,
            "/fc" or "/freecompany" => 6,
            "/pvpt" or "/pvpteam" => 7,
            "/n" or "/novice" or "/b" or "/beginner" => 8,
            _ => 0,
        };

        if (fixedChannel != 0)
            return (fixedChannel, 0);

        foreach (var (longForm, shortForm, firstChannel) in LinkshellForms)
        {
            // Long form first: "/linkshell1" also starts with the short "/l".
            var digit = command.StartsWith(longForm, StringComparison.Ordinal)
                ? command[longForm.Length..]
                : command.StartsWith(shortForm, StringComparison.Ordinal)
                    ? command[shortForm.Length..]
                    : null;

            if (digit is { Length: 1 } && digit[0] is >= '1' and <= '8')
            {
                var index = (uint)(digit[0] - '1');
                return (firstChannel + (int)index, index);
            }
        }

        return null;
    }

    /// <summary>
    /// Leaves the input in the channel a just-sent "/p lmao" addressed, so the
    /// follow-up line goes to the same place — the game itself only does that
    /// for a bare "/p". Tabs with a fixed destination are left alone: their
    /// next line goes where the tab says no matter what the active channel is.
    /// </summary>
    private static void PersistChannel(TabState tab, string text)
    {
        if (tab.IsTell || tab.SendCommand is { Length: > 0 } || text.Length == 0 || text[0] != '/')
            return;

        var end = text.IndexOf(' ');
        var command = (end < 0 ? text : text[..end]).ToLowerInvariant();

        if (ChannelForCommand(command) is { } destination)
            ChangeGameChannel(destination.Channel, destination.Linkshell);
    }

    private void SwitchToNextTab(TabState current, int direction)
    {
        var all = tabs.Snapshot();
        if (all.Length < 2)
            return;

        var index = Array.FindIndex(all, t => t.Id == current.Id);
        selectTabId = all[(index + direction + all.Length) % all.Length].Id;
        focusInput = true;
    }

    /// <summary>
    /// The game's currently active input channel (what an untargeted message
    /// would go to), as color + localized label from the chat log agent.
    /// The numbering follows the game's input channels; ClientStructs only
    /// names the first few, verified against Say/Party/Alliance.
    /// </summary>
    private static unsafe (Vector4 Color, string Label)? ActiveGameChannel()
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return null;

        var type = (int)agent->CurrentChannel switch
        {
            0 => XivChatType.TellOutgoing,
            1 => XivChatType.Say,
            2 => XivChatType.Party,
            3 => XivChatType.Alliance,
            4 => XivChatType.Yell,
            5 => XivChatType.Shout,
            6 => XivChatType.FreeCompany,
            7 => XivChatType.PvPTeam,
            8 => XivChatType.NoviceNetwork,
            // XivChatType linkshell values aren't contiguous (CWLS2+ live in a
            // different range), but they all share one color anyway.
            >= 9 and <= 16 => XivChatType.CrossLinkShell1,
            >= 19 and <= 26 => XivChatType.Ls1,
            _ => XivChatType.Say,
        };

        return (ChatColors.For(type), agent->ChannelLabel.ToString());
    }

    /// <summary>Destination color + label for plain text typed in this tab.</summary>
    private static (Vector4 Color, string Label)? SendDestination(TabState tab)
    {
        if (tab.IsTell)
            return (ChatColors.For(XivChatType.TellOutgoing), $"Tell {tab.Title}");

        var fixedType = tab.SendCommand switch
        {
            "/p" or "/party" => XivChatType.Party,
            "/fc" or "/freecompany" => XivChatType.FreeCompany,
            "/s" or "/say" => XivChatType.Say,
            "/sh" or "/shout" => XivChatType.Shout,
            "/y" or "/yell" => XivChatType.Yell,
            "/a" or "/alliance" => XivChatType.Alliance,
            "/n" or "/novice" => XivChatType.NoviceNetwork,
            _ => (XivChatType?)null,
        };

        if (fixedType is { } type)
            return (ChatColors.For(type), tab.SendCommand!);

        // No per-tab channel: plain text goes to the game's active channel.
        return ActiveGameChannel();
    }

    private void DrawInput(TabState tab)
    {
        inputTab = tab;

        // A failed translated send hands its text back here instead of writing
        // `drafts` from its continuation thread.
        if (pendingDraftRestore is { } restore)
        {
            pendingDraftRestore = null;

            // The tab can be gone by now — a tell tab closed while its send
            // was still translating. Writing the draft back would recreate an
            // entry nothing will ever sweep, so only the notice survives.
            if (Array.Exists(tabs.Snapshot(), t => t.Id == restore.TabId))
                drafts[restore.TabId] = restore.Draft;

            // The widget owns its buffer while focused, so an external draft
            // write is dropped there — route it through the callback like the
            // other programmatic edits. Focus is deliberately not stolen back:
            // the send already handed control to the game and this lands at an
            // arbitrary moment, possibly mid-fight.
            if (restore.TabId == tab.Id && inputActiveLastFrame)
                restoreDraftRequested = restore.Draft;

            Notify(restore.Reason);
        }

        drafts.TryGetValue(tab.Id, out var draft);
        draft ??= string.Empty;

        // Empty-input Tab cycling. Handled at draw level rather than in the
        // completion callback: ImGui doesn't deliver the completion event for
        // an empty buffer (Tab falls through to keyboard nav instead).
        if (inputActiveLastFrame && draft.Length == 0 && ImGui.IsKeyPressed(ImGuiKey.Tab, false))
        {
            var direction = ImGui.GetIO().KeyShift ? -1 : 1;
            if (tab.IsTell || tab.SendCommand is { Length: > 0 })
                SwitchToNextTab(tab, direction);
            else
                CycleGameChannel(direction);

            // Nav may have stolen focus for this frame; take it back.
            focusInput = true;
        }

        // While the field is focused, tint its border with the channel color
        // the message will be sent in, as a destination indicator.
        var destination = SendDestination(tab);
        using var border = ImRaii.PushColor(
            ImGuiCol.Border, destination?.Color ?? default, inputActiveLastFrame && destination.HasValue);

        // A pending tab switch (selectTabId) takes effect next frame; the
        // outgoing tab must not consume the focus request meant for the
        // incoming tab's input.
        if (focusInput && (selectTabId == null || selectTabId == tab.Id))
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

            // Text the game asked to pre-fill into the chat input (e.g. an
            // emote command from a UI button, or <item> from the inventory
            // Link action). Match vanilla: a command replaces the draft, a
            // placeholder appends to it (once).
            if (pendingInsert is { } insert)
            {
                pendingInsert = null;
                if (insert.StartsWith('/'))
                    draft = insert;
                else if (!draft.Contains(insert, StringComparison.Ordinal))
                    draft += insert;
            }
        }

        // While a translated send is in flight the placeholder doubles as its
        // progress indicator: the field stays fully usable, and it is empty
        // right after a submit — exactly when a hint is visible.
        var translating = pendingSend is { IsCompleted: false } && pendingSendTabId == tab.Id;

        // Game Default puts the destination on the prompt line above the bar
        // (as the game does), so the field itself stays empty there.
        var hint = translating
            ? "Translating…"
            : FFTheme.GameLayout
                ? string.Empty
                : tab.IsTell
                    ? $"Message {tab.Title}…"
                    : destination is { Label.Length: > 0 } dest
                        ? $"{dest.Label}…"
                        : "Chat or /command…";
        // A history walk belongs to the tab it started in. Every tab has its
        // own buffer, so carrying the position across tabs would splice this
        // tab's history over another tab's unsent draft without stashing it —
        // and walking back down would then restore the wrong tab's stash.
        if (historyTabId != tab.Id)
        {
            historyTabId = tab.Id;
            historyPos = -1;
            historyStash = string.Empty;
        }

        var inputPos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(-1);

        // Game Default draws the field's frame itself (square where it meets
        // the chat-mode button, one rounded cap on the right), so the widget
        // gets a transparent frame over it. The rect is known before the widget
        // is submitted: SetNextItemWidth(-1) runs it to the window edge.
        using var vanillaFrame = PushGameInputFrame(inputPos, destination?.Color);

        // InputText can't style a substring. With a link placeholder in the
        // draft, the widget draws its text transparent and the visible text
        // (placeholders in link blue, own caret) is repainted on top by
        // DrawInputTextOverlay.
        var placeholders = FindLinkPlaceholders(draft);
        var hasLinkPlaceholder = placeholders.Count > 0;
        var draftBeforeInput = draft;

        bool submitted;
        using (ImRaii.PushColor(ImGuiCol.Text, Vector4.Zero, hasLinkPlaceholder))
        {
            submitted = ImGui.InputTextWithHint(
                $"##input{tab.Id}", hint, ref draft, ChatSender.MaxBytes,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory
                | ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion
                | ImGuiInputTextFlags.CallbackCharFilter,
                InputCallback);
        }
        var inputActive = ImGui.IsItemActive();

        // Dropped if the callback never ran (the field lost focus in the same
        // frame the restore landed): the plain `drafts` write already covers
        // that case, and a stale request would clobber the next thing typed.
        restoreDraftRequested = null;

        if (hasLinkPlaceholder)
        {
            // The widget may have edited the draft this frame; re-scan only then.
            if (!ReferenceEquals(draft, draftBeforeInput))
                placeholders = FindLinkPlaceholders(draft);
            DrawInputTextOverlay(draft, placeholders, inputActive);
        }
        inputActiveLastFrame = inputActive;
        drafts[tab.Id] = draft;

        UpdateSuggestions(draft, inputActive || focusInput);
        if (suggestions.Count > 0)
            DrawSuggestions(tab, inputPos);

        if (!submitted)
            return;

        if (Submit(tab, draft))
        {
            drafts[tab.Id] = string.Empty;
            scrollLogToBottom = true;

            // Match vanilla: sending hands control back to the game (WASD
            // works immediately); Enter re-opens the input.
            ImGuiP.FocusWindow(default);
            suppressEnterUntilReleased = true;
        }
        else
        {
            // Send failed; keep the draft and the focus so it can be fixed.
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    private static readonly string[] LinkPlaceholderTokens = ["<item>", "<flag>", "<status>"];

    private static readonly List<(int Start, int Length)> NoSpans = [];

    private static List<(int Start, int Length)> FindLinkPlaceholders(string draft)
    {
        // Shared empty result for the common case; callers never mutate it.
        if (draft.Length == 0)
            return NoSpans;

        List<(int Start, int Length)> spans = [];

        foreach (var token in LinkPlaceholderTokens)
        {
            var from = 0;
            int index;
            while (from < draft.Length
                   && (index = draft.IndexOf(token, from, StringComparison.Ordinal)) >= 0)
            {
                spans.Add((index, token.Length));
                from = index + token.Length;
            }
        }

        spans.Sort();
        return spans;
    }

    /// <summary>
    /// Repaints the input field's text (drawn transparent by the widget when
    /// a link placeholder is present): normal runs in the regular text color,
    /// placeholders in the link color, plus a caret, honoring the field's
    /// internal horizontal scroll. Must run directly after the InputText so
    /// the item rect and id still refer to it.
    /// </summary>
    private void DrawInputTextOverlay(string draft, List<(int Start, int Length)> spans, bool inputActive)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var pad = ImGui.GetStyle().FramePadding;

        var scrollX = 0f;
        var cursor = -1;
        if (inputActive)
        {
            var id = ImGuiP.GetItemID();
            var state = ImGuiP.GetInputTextState(id);
            if (!state.IsNull && state.ID == id)
            {
                scrollX = state.ScrollX;
                cursor = Math.Clamp(state.Stb.Cursor, 0, draft.Length);
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(
            new Vector2(min.X + pad.X, min.Y),
            new Vector2(max.X - pad.X, max.Y),
            true);

        var normalColor = ImGui.GetColorU32(ImGuiCol.Text);
        var linkColor = ImGui.GetColorU32(ChatColors.Link);
        var pos = new Vector2(min.X + pad.X - scrollX, min.Y + pad.Y);

        void DrawRun(int start, int end, uint color)
        {
            if (end <= start)
                return;

            var run = draft[start..end];
            drawList.AddText(pos, color, run);
            pos.X += ImGui.CalcTextSize(run).X;
        }

        var previous = 0;
        foreach (var (start, length) in spans)
        {
            DrawRun(previous, start, normalColor);
            DrawRun(start, start + length, linkColor);
            previous = start + length;
        }

        DrawRun(previous, draft.Length, normalColor);

        // The widget's own caret is transparent along with its text; draw a
        // replacement. Free-running blink (the real one resets on keypress).
        if (cursor >= 0 && (ImGui.GetTime() % 1.2) <= 0.8)
        {
            var x = min.X + pad.X - scrollX + ImGui.CalcTextSize(draft[..cursor]).X;
            drawList.AddLine(
                new Vector2(x, min.Y + pad.Y),
                new Vector2(x, max.Y - pad.Y),
                normalColor);
        }

        drawList.PopClipRect();
    }

    /// <summary>
    /// A command whose first argument is a name, completed from the world
    /// instead of the command list. A tell reaches anyone, so its candidates
    /// come from every source and carry "@World"; /target and /examine act on
    /// an entity in the object table, so they only offer what is actually
    /// nearby — and /target is the game's own command, which takes a bare name
    /// (and any targetable object, not just players).
    /// </summary>
    private sealed record NameArgCommand(
        string[] Forms,
        bool NearbyOnly = false,
        bool WithWorld = true,
        bool IncludeNpcs = false);

    private static readonly string[] TargetCommands = ["/target"];

    private static readonly NameArgCommand[] NameArgCommands =
    [
        new(["/tell", "/t"]),
        new(TargetCommands, NearbyOnly: true, WithWorld: false, IncludeNpcs: true),
        new([Plugin.ExamineCommand], NearbyOnly: true),
        new([Plugin.MountCommand], NearbyOnly: true),
    ];

    /// <summary>Trailing ":xx" emote partial with the colon at a word start.</summary>
    private static readonly System.Text.RegularExpressions.Regex EmotePartial =
        new(@"(?:^|\s):([\w+\-]{2,})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void UpdateSuggestions(string draft, bool inputActive)
    {
        // Emote completion fires anywhere in the message, including inside a
        // command's text argument, so it runs before the command logic.
        if (inputActive && plugin.Configuration.RenderEmotes)
        {
            var emote = EmotePartial.Match(draft);
            if (emote.Success)
            {
                if (draft != suggestionQuery)
                {
                    suggestionQuery = draft;
                    var prefix = draft[..emote.Groups[1].Index]; // ends with the colon
                    suggestions = Emotes.Query(emote.Groups[1].Value, 8)
                        .Select(e => new CommandEntry(
                            $"{prefix}{e.Name}:", string.Empty,
                            Display: $":{e.Name}:", Emote: e.Emoji))
                        .ToList();
                    suggestionIndex = 0;
                }

                return;
            }
        }

        var wantSuggestions = inputActive && draft.Length > 1 && draft[0] == '/';

        // "/tell " (or "/t ", "/target ", "/examine ") switches from command
        // completion to completing the name argument. Once the typed text stops
        // prefixing any known name (i.e. the message part began), the popup
        // disappears on its own.
        NameArgCommand? nameCommand = null;
        var nameCommandText = string.Empty;
        var namePartial = string.Empty;
        if (wantSuggestions)
        {
            foreach (var candidate in NameArgCommands)
            {
                foreach (var command in candidate.Forms)
                {
                    if (draft.Length > command.Length
                        && draft[command.Length] == ' '
                        && draft.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                    {
                        nameCommand = candidate;
                        nameCommandText = draft[..command.Length];
                        namePartial = draft[(command.Length + 1)..];
                        break;
                    }
                }

                if (nameCommand != null)
                    break;
            }

            if (nameCommand == null && draft.Contains(' '))
                wantSuggestions = false;
        }

        if (!wantSuggestions)
        {
            suggestions = [];
            suggestionQuery = string.Empty;
            return;
        }

        if (draft != suggestionQuery)
        {
            suggestionQuery = draft;
            suggestions = nameCommand != null
                ? QueryNames(nameCommand, nameCommandText, namePartial)
                : commandIndex.Query(draft);
            suggestionIndex = 0;
        }
    }

    /// <summary>
    /// Name suggestions for a partial name argument, as full commands so the
    /// whole-buffer acceptance path works unchanged. Sources in priority order:
    /// open tell tabs, party, friends, then whatever is nearby — the last of
    /// which is sorted by distance, since in a crowd the name you want is
    /// nearly always the one standing in front of you.
    /// </summary>
    private List<CommandEntry> QueryNames(NameArgCommand spec, string command, string partial)
    {
        var candidates = new List<(string Key, string Source)>();

        // Anyone not in the object table is unreachable for /target and
        // /examine, so those skip the remote sources entirely.
        if (!spec.NearbyOnly)
        {
            foreach (var partner in tabs.TellPartners())
                candidates.Add((partner, "tell tab"));

            foreach (var member in Plugin.PartyList)
            {
                var name = member.Name.TextValue;
                if (name.Length > 0)
                    candidates.Add((GameData.WithWorld(name, member.World.RowId), "party"));
            }

            foreach (var friend in plugin.Presence.FriendNames())
                candidates.Add((friend, "friend"));
        }

        var origin = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var nearby = new List<(string Key, string Source, float Distance)>();
        foreach (var obj in Plugin.ObjectTable)
        {
            var name = obj.Name.TextValue;
            if (name.Length == 0)
                continue;

            var distance = Vector3.Distance(origin, obj.Position);
            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
            {
                if (name == localFullName)
                    continue;

                var key = spec.WithWorld ? GameData.WithWorld(name, player.HomeWorld.RowId) : name;
                nearby.Add((key, "nearby", distance));
            }
            else if (spec.IncludeNpcs && obj.IsTargetable)
            {
                nearby.Add((name, "npc", distance));
            }
        }

        nearby.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        foreach (var (key, source, _) in nearby)
            candidates.Add((key, source));

        return candidates
            .Where(c => c.Key.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(c => new CommandEntry($"{command} {c.Key}", c.Source))
            .ToList();
    }

    /// <summary>
    /// Handles "/target Full Name" locally when the name matches something
    /// nearby. False for anything else, including an unmatched name, so the
    /// text goes out to the game unchanged.
    /// </summary>
    private static bool ResolveTarget(string text)
    {
        foreach (var command in TargetCommands)
        {
            if (text.Length > command.Length
                && text[command.Length] == ' '
                && text.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                return PlayerActions.TargetByName(text[(command.Length + 1)..].Trim());
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces the draft with the highlighted completion (plus a trailing
    /// space) and refocuses the input. Clears the popup and seeds the query so
    /// it doesn't immediately reopen on the completed text.
    /// </summary>
    private void AcceptSuggestion(TabState tab)
    {
        var completed = suggestions[Math.Clamp(suggestionIndex, 0, suggestions.Count - 1)].Command + " ";
        drafts[tab.Id] = completed;
        focusInput = true;
        suggestions = [];
        suggestionQuery = completed;
    }

    private void DrawSuggestions(TabState tab, Vector2 inputPos)
    {
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = suggestions.Count * lineHeight + ImGui.GetStyle().WindowPadding.Y * 2;
        var width = Math.Max(320f, ImGui.GetWindowWidth() * 0.6f);

        ImGui.SetNextWindowPos(new Vector2(inputPos.X, inputPos.Y - height - 4));
        ImGui.SetNextWindowSize(new Vector2(width, height));

        // The Tooltip flag moves the window to the tooltip z-layer so it always
        // draws above the chat window; unlike BeginTooltip it stays clickable.
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.Tooltip;

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

            if (entry.Emote != null && Emotes.GetTexture(entry.Emote) is { } emoteWrap)
            {
                var iconSize = ImGui.GetTextLineHeight();
                ImGui.Image(emoteWrap.Handle, new Vector2(iconSize, iconSize));
                ImGui.SameLine();
            }

            if (ImGui.Selectable($"{entry.Display ?? entry.Command}##sugg{i}", selected))
            {
                suggestionIndex = i;
                AcceptSuggestion(tab);
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
            // A failed translated send handing its text back while the field
            // is still focused. Same reason as the Space acceptance below: an
            // external draft write is ignored once the widget owns its buffer.
            if (restoreDraftRequested is { } restored)
            {
                restoreDraftRequested = null;
                data.DeleteChars(0, data.BufTextLen);
                data.InsertChars(0, restored);
            }

            // Programmatic focus selects the whole buffer; typing would then
            // replace it. Put the cursor at the end with nothing selected.
            if (clearSelection)
            {
                clearSelection = false;
                data.CursorPos = data.BufTextLen;
                data.SelectionStart = data.BufTextLen;
                data.SelectionEnd = data.BufTextLen;
            }

            // Space accepted a completion (flagged in the char filter earlier
            // this same frame). Edit the widget's own buffer here — an external
            // draft write is ignored while the field is active, so it must go
            // through the callback like the Tab path does.
            if (acceptSuggestionRequested)
            {
                acceptSuggestionRequested = false;
                if (suggestions.Count > 0)
                {
                    var completed =
                        suggestions[Math.Clamp(suggestionIndex, 0, suggestions.Count - 1)].Command + " ";
                    data.DeleteChars(0, data.BufTextLen);
                    data.InsertChars(0, completed);
                }
            }

            return 0;
        }

        // In Tab-cycles mode, Space accepts the highlighted completion. The
        // char is discarded here (return 1) and flagged; CallbackAlways fires
        // later this same frame and applies the edit to the widget's buffer.
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCharFilter)
        {
            if (data.EventChar == ' '
                && suggestions.Count > 0
                && plugin.Configuration.TabCyclesSuggestions)
            {
                acceptSuggestionRequested = true;
                return 1;
            }

            return 0;
        }

        // Tab: with text, accept the highlighted suggestion; on an empty
        // input, cycle chats (fixed-destination tabs) or the game's active
        // channel (General/System), which updates the border indicator.
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (suggestions.Count > 0)
            {
                // Tab-cycles mode: move the highlight (Shift = backward) and
                // wait for Space to lock it in, rather than accepting now.
                if (plugin.Configuration.TabCyclesSuggestions)
                {
                    var direction = ImGui.GetIO().KeyShift ? -1 : 1;
                    suggestionIndex =
                        (suggestionIndex + direction + suggestions.Count) % suggestions.Count;
                    return 0;
                }

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

        // A recalled command must not pop the suggestion box, or the next
        // Up/Down moves its highlight instead of walking history. Seeding
        // the query keeps it suppressed until the text is actually edited.
        suggestions = [];
        suggestionQuery = replacement;
        return 0;
    }

    /// <summary>
    /// True when the draft was consumed (clear it and release focus), false to
    /// keep both so the user can fix it. A send needing outgoing translation
    /// reports consumed immediately and finishes asynchronously.
    /// </summary>
    private bool Submit(TabState tab, string draft)
    {
        var text = draft.Trim();
        if (text.Length == 0)
            return true;

        // The game's /target reads its argument only up to the first space, so
        // it rejects any full player name ("Erik Jeannek" comes back as "Erik
        // is not a valid target name") — which is exactly what the completion
        // hands it. Resolve against the object table first; an unresolved name
        // still goes to the game, which owns placeholders and partial matches.
        if (ResolveTarget(text))
        {
            RecordHistory(text);
            return true;
        }

        // Commands address the game, not a person, so they are never
        // translated and keep the synchronous path exactly as it was.
        if (text[0] != '/' && plugin.Configuration.TranslateOutgoing)
        {
            // Rejected rather than queued: translations complete out of order,
            // so a queue could put a later line on the wire before an earlier
            // one, and the window being contested is a single round trip.
            if (pendingSend is { IsCompleted: false })
            {
                Notify("Still translating the previous message.");
                return false;
            }

            // Recorded here, on the draw thread, and from the ORIGINAL text:
            // history is what the user typed, not what went out.
            RecordHistory(text);
            pendingSendTabId = tab.Id;
            pendingSend = SendTranslatedAsync(tab, text);
            return true;
        }

        if (!ChatSender.Send(text[0] == '/' ? text : ApplyDestination(tab, text)))
        {
            // The only false path is the length cap — easy to hit in a tell
            // tab, where "/tell Name@World " is prepended after the input's
            // own limit. Without feedback, Enter appears to do nothing.
            Notify("Message too long to send.");
            return false;
        }

        RecordHistory(text);
        PersistChannel(tab, text);
        return true;
    }

    /// <summary>Prefixes the tab's destination onto a plain (non-command) message.</summary>
    private static string ApplyDestination(TabState tab, string text) =>
        tab.IsTell
            ? $"/tell {tab.TellPartner} {text}"
            : tab.SendCommand is { Length: > 0 } sendCommand
                ? $"{sendCommand} {text}"
                : text;

    private void RecordHistory(string text)
    {
        if (sentHistory.Count == 0 || sentHistory[^1] != text)
        {
            sentHistory.Add(text);
            if (sentHistory.Count > MaxHistory)
                sentHistory.RemoveAt(0);
        }

        historyPos = -1;
        historyStash = string.Empty;
    }

    // Past this the input has been sitting empty long enough that a message
    // finally going out is more surprising than one that never did.
    private static readonly TimeSpan OutgoingTranslateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Translates a typed line and sends the translation in its place (the
    /// original is not appended). Runs off the draw thread, so a failure hands
    /// the text back through <see cref="pendingDraftRestore"/> rather than
    /// touching the drafts, the history or the notification queue from here.
    /// </summary>
    private async Task SendTranslatedAsync(TabState tab, string text)
    {
        string? failure;
        try
        {
            using var cts = new CancellationTokenSource(OutgoingTranslateTimeout);
            var translated = await plugin.Translation
                .TranslateOutgoingAsync(text, cts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(translated))
            {
                // The provider's own reason when there is one: the common case
                // is outgoing translation switched on with no API key behind
                // it, and "translation failed" alone gives nothing to act on.
                failure = plugin.Translation.LastError is { Length: > 0 } reason
                    ? $"Not sent — {reason}"
                    : "Translation failed; message not sent.";
            }
            else
            {
                // Send re-checks the UTF-8 cap and false means only that —
                // the check that matters here, because a translation is easily
                // longer than what was typed and the prefix lands on top of it.
                failure = ChatSender.Send(ApplyDestination(tab, translated))
                    ? null
                    : "Translation too long to send.";
            }
        }
        catch (OperationCanceledException)
        {
            failure = "Translation timed out; message not sent.";
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Outgoing translation failed");
            failure = "Translation failed; message not sent.";
        }

        // Published before this method completes, so the draw thread can never
        // observe pendingSend as finished without also seeing the restore.
        if (failure != null)
            pendingDraftRestore = new PendingRestore(tab.Id, text, failure);
    }
}
