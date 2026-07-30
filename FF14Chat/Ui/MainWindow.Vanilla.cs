using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using FF14Chat.Services;

namespace FF14Chat.Ui;

/// <summary>
/// The Game Default theme's layout, which follows the game's own chat log
/// rather than the plugin's: no panel behind the log, the input row under it
/// (round chat-mode button plus a bar with one rounded cap), and the tab strip
/// under that as a single glossy pill of segments with round buttons beside it.
///
/// The other themes let ImGui's tab bar own tab selection and reordering; this
/// strip is hand-drawn, so selection, dragging, closing and scrolling live
/// here instead.
/// </summary>
public partial class MainWindow
{
    /// <summary>Selected tab in Game Default (ImGui's tab bar owns it elsewhere).</summary>
    private string? gameTabId;

    private float gameTabScroll;

    /// <summary>Selection the strip has already scrolled to; see DrawGameTabStrip.</summary>
    private string? followedTabId;

    // Hover is read one frame late so a tell tab's close cross can be
    // submitted BEFORE the cell it sits on (the earliest overlapping item
    // wins the click).
    private string? hoveredGameTabId;

    /// <summary>0..1 reveal of the window controls the game itself has no room for.</summary>
    private float controlsFade;

    /// <summary>A press on empty log space or the strip; drags the window until released.</summary>
    private bool windowDragArmed;

    private readonly List<string> gameOrderScratch = [];

    private const string ChannelMenuPopup = "chatmode-menu";

    private const float StripGap = 3f;

    /// <summary>Width reserved for the close cross at the right of every cell.</summary>
    private const float TellCloseGutter = 18f;

    /// <summary>Width reserved for a tell tab's presence dot at the left of its cell.</summary>
    private const float PresenceGutter = 14f;

    private void DrawGameLayout()
    {
        // Before anything is submitted: SetWindowPos only moves what is drawn
        // AFTER it, so applying a drag mid-frame would tear the log away from
        // the rows below it. The handles below merely arm the drag; the move
        // lands here, on the next frame. (The other themes get this for free —
        // their drag handle is the header, the first thing they draw.)
        ApplyWindowDrag();

        var visible = VisibleTabs();

        // Lock/close have no counterpart in the game's strip, so they fade in
        // with the pointer instead of taking permanent space.
        var windowHovered = ImGui.IsWindowHovered(
            ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var fadeTarget = windowHovered || inputActiveLastFrame ? 1f : 0f;
        controlsFade = Math.Clamp(
            controlsFade + (fadeTarget - controlsFade) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 12f),
            0f, 1f);

        DrawGameBackdrop();

        var rowHeight = ImGui.GetFrameHeight();

        // Every window control lives on the strip in this theme, so a state
        // with no visible tabs (all fixed tabs FC-only while not in one, last
        // tell tab closed) must still draw it — otherwise the window is blank
        // with no way to open settings or close it.
        if (visible.Length == 0)
        {
            var emptyY = ImGui.GetCursorPosY() + ImGui.GetContentRegionAvail().Y - rowHeight;
            DrawGameTabStrip(visible, null, ImGui.GetCursorPosX(), emptyY, rowHeight);
            DrawGameResizeGrip();
            return;
        }

        var tab = ResolveGameTab(visible);
        tabs.MarkRead(tab);

        if (focusInput)
            ImGui.SetWindowFocus();

        var promptHeight = ImGui.GetTextLineHeight();

        // An armed <item> placeholder shows a preview line, same as the other
        // themes; it eats into the log, not into the input row.
        drafts.TryGetValue(tab.Id, out var pendingDraft);
        var linkPreview = pendingDraft?.Contains("<item>", StringComparison.Ordinal) == true
            ? LinkedItemName()
            : null;
        var previewHeight = linkPreview != null ? ImGui.GetTextLineHeightWithSpacing() : 0f;

        var top = ImGui.GetCursorPosY();
        var left = ImGui.GetCursorPosX();
        var stripY = top + ImGui.GetContentRegionAvail().Y - rowHeight;
        var inputY = stripY - StripGap - rowHeight;
        var promptY = inputY - promptHeight;
        var previewY = promptY - previewHeight;
        var logHeight = MathF.Max(previewY - top - StripGap, rowHeight);

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2)))
        using (var child = ImRaii.Child("##log", new Vector2(-1, logHeight), false))
        {
            if (child.Success)
            {
                DrawLog(tab);
                ArmLogDrag();
            }
        }

        if (linkPreview != null)
        {
            ImGui.SetCursorPos(new Vector2(left, previewY));
            ImGui.TextColored(ChatColors.Link, $"{SeIconChar.LinkMarker.ToIconChar()} {linkPreview}");
        }

        DrawGamePrompt(tab, left, promptY, rowHeight);
        DrawGameInputRow(tab, left, inputY, rowHeight);
        DrawGameTabStrip(visible, tab, left, stripY, rowHeight);
        DrawGameResizeGrip();
    }

    /// <summary>
    /// Without a header there is nothing to grab, so empty space in the log
    /// drags the window. Armed on a press that landed on no item (a click on a
    /// link or a message row belongs to that row), then held until release so
    /// the drag survives the cursor crossing text.
    /// </summary>
    private void ArmLogDrag()
    {
        if (ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered()
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            windowDragArmed = true;
        }
    }

    /// <summary>
    /// Moves the window for a drag armed by the log or the strip on an earlier
    /// frame. Runs first in the frame; see the call site for why.
    /// </summary>
    private void ApplyWindowDrag()
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            windowDragArmed = false;
            return;
        }

        if (windowDragArmed && !plugin.Configuration.LockWindow
            && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }
    }

    /// <summary>
    /// The window's only resize handle in this theme: PreDraw sets NoResize so
    /// ImGui's native grip can't claim the corner ahead of the strip buttons.
    /// It also gives the corner something visible to grab, which the native
    /// grip never had here — with no window background there was nothing to see.
    /// </summary>
    private void DrawGameResizeGrip()
    {
        if (plugin.Configuration.LockWindow)
            return;

        var corner = ImGui.GetWindowPos() + ImGui.GetWindowSize();
        const float grip = 14f;

        ImGui.SetCursorScreenPos(corner - new Vector2(grip, grip));
        ImGui.InvisibleButton("##resize-grip", new Vector2(grip, grip));
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered() || active;

        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            var wanted = ImGui.GetWindowSize() + ImGui.GetIO().MouseDelta;
            ImGui.SetWindowSize(new Vector2(MathF.Max(wanted.X, 250f), MathF.Max(wanted.Y, 150f)));
        }

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);

        var color = ImGui.GetColorU32(VanillaChrome.LabelDim with { W = hovered ? 0.90f : 0.40f });
        var drawList = ImGui.GetWindowDrawList();
        for (var i = 1; i <= 3; i++)
        {
            var offset = i * 4f;
            drawList.AddLine(
                corner - new Vector2(offset, 2f), corner - new Vector2(2f, offset), color, 1.4f);
        }
    }

    /// <summary>
    /// The soft dark pane the game keeps behind its log for contrast: always
    /// there, faint, with feathered edges rather than a hard rectangle. It
    /// deepens while the window is hovered or the input is active, which is
    /// what the game does when the chat takes focus.
    /// </summary>
    private void DrawGameBackdrop()
    {
        var backdrop = FFTheme.LogBackdrop;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        // Idle sits well under the focused level so the world stays readable
        // through it; the opacity slider scales both.
        var alpha = backdrop.W * (0.42f + 0.48f * controlsFade);

        VanillaChrome.SoftPanel(
            pos + new Vector2(2f, 2f), pos + size - new Vector2(2f, 2f),
            backdrop, alpha, FFTheme.Rounding, 7f);
    }

    /// <summary>Tabs the strip may show; the FC tab is hidden without a free company.</summary>
    private TabState[] VisibleTabs()
    {
        var snapshot = tabs.Snapshot();
        return UpdateFreeCompanyLatch() ? snapshot : Array.FindAll(snapshot, t => !IsFcOnlyTab(t));
    }

    /// <summary>Applies a pending selection request and falls back to the first tab.</summary>
    private TabState ResolveGameTab(TabState[] visible)
    {
        if (selectTabId is { } requested)
        {
            // A request naming a hidden or closed tab must not linger: the
            // input's focus handling waits on it.
            if (Array.Exists(visible, t => t.Id == requested))
                gameTabId = requested;
            selectTabId = null;
        }

        var current = Array.Find(visible, t => t.Id == gameTabId);
        if (current != null)
            return current;

        gameTabId = visible[0].Id;
        return visible[0];
    }

    /// <summary>Channel prompt over the input bar ("Say", "Tell Name", …).</summary>
    private void DrawGamePrompt(TabState tab, float left, float y, float rowHeight)
    {
        var label = SendDestination(tab)?.Label;
        if (string.IsNullOrEmpty(label))
            return;

        ImGui.SetCursorPos(new Vector2(left + rowHeight + 8f, y));
        VanillaChrome.Text(ImGui.GetCursorScreenPos(), VanillaChrome.Prompt, label);
    }

    private void DrawGameInputRow(TabState tab, float left, float y, float rowHeight)
    {
        var alpha = FFTheme.ChromeAlpha;

        ImGui.SetCursorPos(new Vector2(left, y));
        var bubbleMin = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton("##chatmode", new Vector2(rowHeight, rowHeight));
        var hovered = ImGui.IsItemHovered();
        var center = bubbleMin + new Vector2(rowHeight * 0.5f, rowHeight * 0.5f);

        VanillaChrome.Circle(center, rowHeight * 0.5f - 0.5f, alpha, hovered);
        VanillaChrome.BubbleGlyph(
            center, rowHeight * 0.52f,
            ImGui.GetColorU32(hovered ? VanillaChrome.Label : VanillaChrome.LabelDim));

        if (hovered)
            ImGui.SetTooltip("Chat mode");
        if (pressed)
            ImGui.OpenPopup(ChannelMenuPopup);

        DrawChannelMenu();

        // The field starts right of the button; DrawInput's SetNextItemWidth(-1)
        // then runs it to the window edge, and its Game Default branch draws the
        // capped bar behind it.
        ImGui.SetCursorPos(new Vector2(left + rowHeight + 2f, y));
        DrawInput(tab);
    }

    private void DrawChannelMenu()
    {
        using var popup = ImRaii.Popup(ChannelMenuPopup);
        if (!popup.Success)
            return;

        foreach (var (label, channel) in GameChannels)
        {
            if (ImGui.Selectable(label))
                GameKeybindChannel(channel, 0);
        }
    }

    // RaptureShell channel numbers; see ChannelSendCommands.
    private static readonly (string Label, int Channel)[] GameChannels =
    [
        ("Say", 1),
        ("Shout", 5),
        ("Yell", 4),
        ("Party", 2),
        ("Alliance", 3),
        ("Free Company", 6),
        ("Novice Network", 8),
    ];

    private readonly List<(TabState Tab, Vector2 Min, Vector2 Max)> gameTabCells = [];

    private void DrawGameTabStrip(TabState[] visible, TabState? selected, float left, float y, float height)
    {
        var alpha = FFTheme.ChromeAlpha;
        var radius = height * 0.5f;
        var capWidth = radius + 4f;
        var buttonSize = height;
        var buttonGap = 3f;

        // Room for all four buttons is reserved even while lock/close are
        // faded out, so the strip doesn't resize as they appear.
        var buttonsWidth = 4f * buttonSize + 3f * buttonGap;

        ImGui.SetCursorPos(new Vector2(left, y));
        var origin = ImGui.GetCursorScreenPos();
        var available = MathF.Max(ImGui.GetContentRegionAvail().X - buttonsWidth - 6f, capWidth * 2f);

        // Measured unscrolled, so this frame's clamp and the follow-the-selection
        // nudge both apply to the cells actually drawn (no one-frame lag).
        MeasureGameTabCells(visible, origin, capWidth, height);
        var contentWidth = gameTabCells.Count > 0
            ? gameTabCells[^1].Max.X - origin.X + capWidth
            : capWidth * 2f;

        var pillWidth = MathF.Min(contentWidth, available);
        var overflow = MathF.Max(0f, contentWidth - available);
        gameTabScroll = Math.Clamp(gameTabScroll, 0f, overflow);

        // Only when the selection actually changed: running this every frame
        // pulled the strip straight back to the selected tab, which made
        // scrolling look like it did nothing.
        if (selected != null && followedTabId != selected.Id)
        {
            ScrollSelectedTabIntoView(selected, origin, pillWidth, overflow);
            followedTabId = selected.Id;
        }

        ShiftGameTabCells(gameTabScroll);

        var pillMin = origin;
        var pillMax = origin + new Vector2(pillWidth, height);
        VanillaChrome.Pill(pillMin, pillMax, alpha);

        DrawGameTabCells(visible, selected, pillMin, pillMax, height, alpha);
        HandleStripScroll(pillMin, pillMax, overflow);

        // Drag-to-move: submitted after the cells so they keep their clicks
        // (the earliest overlapping item wins), covering the strip's own body.
        // Arms only — ApplyWindowDrag moves the window at the top of the next
        // frame, before anything has been drawn at the old position.
        ImGui.SetCursorScreenPos(pillMin);
        ImGui.InvisibleButton("##strip-drag", pillMax - pillMin);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            windowDragArmed = true;

        // Screen space: `y` is window-local, the pill's own Y is not.
        DrawStripButtons(new Vector2(pillMax.X + 6f, pillMin.Y), buttonSize, buttonGap, alpha);

        // Same ID scope its OpenPopup was called in (the "+" button's).
        DrawNewTabPopup();
    }

    /// <summary>Lays out the cells of the strip, left cap first, into <see cref="gameTabCells"/>.</summary>
    private void MeasureGameTabCells(TabState[] visible, Vector2 origin, float capWidth, float height)
    {
        gameTabCells.Clear();
        var x = origin.X + capWidth;

        foreach (var tab in visible)
        {
            // Every cell reserves the close gutter on the right, tells also a
            // presence dot on the left; neither may crowd the name.
            var width = MathF.Max(
                56f,
                ImGui.CalcTextSize(tab.Title).X + 22f + TellCloseGutter + (tab.IsTell ? PresenceGutter : 0f));
            gameTabCells.Add((tab, new Vector2(x, origin.Y), new Vector2(x + width, origin.Y + height)));
            x += width;
        }
    }

    private void ShiftGameTabCells(float scroll)
    {
        if (scroll == 0f)
            return;

        var offset = new Vector2(scroll, 0f);
        for (var i = 0; i < gameTabCells.Count; i++)
        {
            var (tab, min, max) = gameTabCells[i];
            gameTabCells[i] = (tab, min - offset, max - offset);
        }
    }

    /// <summary>Keeps the selected tab inside the visible part of an overflowing strip.</summary>
    private void ScrollSelectedTabIntoView(TabState selected, Vector2 origin, float pillWidth, float overflow)
    {
        if (overflow <= 0f)
            return;

        var cell = gameTabCells.Find(c => c.Tab.Id == selected.Id);
        if (cell.Tab == null)
            return;

        // Cells are still unscrolled here, so compare against the window the
        // current scroll exposes.
        var leftEdge = origin.X + gameTabScroll;
        var rightEdge = leftEdge + pillWidth;
        if (cell.Min.X < leftEdge)
            gameTabScroll = Math.Clamp(gameTabScroll - (leftEdge - cell.Min.X), 0f, overflow);
        else if (cell.Max.X > rightEdge)
            gameTabScroll = Math.Clamp(gameTabScroll + (cell.Max.X - rightEdge), 0f, overflow);
    }

    private void HandleStripScroll(Vector2 pillMin, Vector2 pillMax, float overflow)
    {
        if (overflow <= 0f)
            return;

        // Geometric hit-test, not IsWindowHovered: an active/hovered cell
        // suppresses the plain window-hover test (same reason as the other
        // themes' strip; see UpdateTabScroll).
        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f && ImGui.IsMouseHoveringRect(pillMin, pillMax))
            gameTabScroll = Math.Clamp(gameTabScroll - wheel * 60f, 0f, overflow);
    }

    private void DrawGameTabCells(
        TabState[] visible, TabState? selected, Vector2 pillMin, Vector2 pillMax, float height, float alpha)
    {
        // Cells are clipped to the pill so a scrolled-out tab is neither drawn
        // nor clickable.
        ImGui.PushClipRect(pillMin, pillMax, true);

        var hoveredThisFrame = (string?)null;
        var lineHeight = ImGui.GetTextLineHeight();

        for (var i = 0; i < gameTabCells.Count; i++)
        {
            var (tab, min, max) = gameTabCells[i];
            var isSelected = tab.Id == selected?.Id;

            // The game rules off every cell, including the blank caps at each
            // end of the pill.
            VanillaChrome.Divider(min, height, alpha);
            if (i == gameTabCells.Count - 1)
                VanillaChrome.Divider(new Vector2(max.X, min.Y), height, alpha);

            if (isSelected)
            {
                // The game marks the active tab with a dot, and lifts its cell
                // a shade rather than filling it with a different color.
                ImGui.GetWindowDrawList().AddRectFilled(
                    min + new Vector2(0f, 1f), max - new Vector2(0f, 1f),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.09f * alpha)));
            }

            // The cross owns its corner of the cell, so its hover has to count
            // as hovering the tab — otherwise it vanishes the moment the
            // pointer reaches it and takes its own click with it.
            //
            // Closing a configured tab deletes it from the config, which
            // CloseGameTab refuses for the last one — count configured tabs,
            // not cells, or open tell tabs would put an inert cross on it.
            var closable = tab.IsTell || plugin.Configuration.Tabs.Count > 1;
            if (closable && hoveredGameTabId == tab.Id && DrawGameTabClose(tab, max, height))
                hoveredThisFrame = tab.Id;

            ImGui.SetCursorScreenPos(min);
            var clicked = ImGui.InvisibleButton($"##tabcell-{tab.Id}", max - min);
            var hovered = ImGui.IsItemHovered();
            if (hovered)
                hoveredThisFrame = tab.Id;

            if (clicked)
            {
                gameTabId = tab.Id;
                focusInput = true;
            }

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle) && closable)
                CloseGameTab(tab);

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && tab.IsTell)
            {
                contextPartner = tab.TellPartner;
                ImGui.OpenPopup(tabContextPopupId);
            }

            HandleGameTabDrag(tab, visible);

            var labelColor = isSelected
                ? VanillaChrome.Label
                : tab.Unread > 0
                    ? VanillaChrome.Unread
                    : hovered ? VanillaChrome.Label : VanillaChrome.LabelDim;

            // Centered in what's left of the cell once the dot and the cross
            // have taken their gutters, so the name never sits under either.
            var textSize = ImGui.CalcTextSize(tab.Title);
            var textLeft = min.X + (tab.IsTell ? PresenceGutter : 0f);
            var textRight = max.X - TellCloseGutter;
            var textPos = new Vector2(
                (textLeft + textRight - textSize.X) * 0.5f,
                min.Y + (height - lineHeight) * 0.5f);
            VanillaChrome.Text(textPos, labelColor, tab.Title);

            if (isSelected)
            {
                ImGui.GetWindowDrawList().AddCircleFilled(
                    new Vector2(min.X + 7f, min.Y + height * 0.5f), 2.4f,
                    ImGui.GetColorU32(VanillaChrome.ActiveDot), 12);
            }

            if (tab.IsTell && plugin.Configuration.ShowTellPresence)
                DrawGamePresenceDot(tab, min, height);

            if (!isSelected && tab.Unread > 0)
                DrawGameUnreadCount(tab, min, max);
        }

        ImGui.PopClipRect();
        hoveredGameTabId = hoveredThisFrame;

        DrawPlayerContextMenu();
    }

    /// <summary>Close cross for a hovered tell tab. Submitted before its cell so it wins the click.</summary>
    private bool DrawGameTabClose(TabState tab, Vector2 cellMax, float height)
    {
        var size = height * 0.62f;
        var center = new Vector2(cellMax.X - TellCloseGutter * 0.5f, cellMax.Y - height * 0.5f);

        ImGui.SetCursorScreenPos(center - new Vector2(size * 0.5f, size * 0.5f));
        var clicked = ImGui.InvisibleButton($"##tabclose-{tab.Id}", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        VanillaChrome.CrossGlyph(
            center, size * 0.62f,
            ImGui.GetColorU32(hovered ? VanillaChrome.Label : VanillaChrome.LabelDim));

        if (clicked)
            CloseGameTab(tab);

        return hovered;
    }

    /// <summary>
    /// Draws the input field's frame for Game Default and hands back the
    /// transparent frame colors the widget itself should use. A no-op push in
    /// the other themes, which let ImGui draw their frame.
    /// </summary>
    private ImRaii.ColorDisposable PushGameInputFrame(Vector2 pos, Vector4? destination)
    {
        if (!FFTheme.GameLayout)
            return ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero, false);

        var width = MathF.Max(ImGui.GetContentRegionAvail().X - 1f, 1f);
        var rim = inputActiveLastFrame && destination is { } color
            ? color
            : new Vector4(0.720f, 0.720f, 0.710f, 1f);

        VanillaChrome.InputBar(
            pos, pos + new Vector2(width, ImGui.GetFrameHeight()), rim, FFTheme.ChromeAlpha);

        return ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero)
            .Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
            .Push(ImGuiCol.FrameBgActive, Vector4.Zero);
    }

    /// <summary>
    /// Closing a tell tab just drops it (its history is backfilled from the
    /// store if the partner writes again); closing a configured tab removes
    /// it from the config, which is what the settings editor's Delete does.
    /// </summary>
    private void CloseGameTab(TabState tab)
    {
        if (tab.IsTell)
        {
            tabs.Close(tab);
        }
        else
        {
            var config = plugin.Configuration;
            if (config.Tabs.Count <= 1)
                return;

            config.Tabs.RemoveAll(t => "tab:" + t.Name == tab.Id);
            config.TabOrder.Remove(tab.Id);
            SettingsWindow.EnsureCombineStillValid(config);
            config.Save();
            tabs.RebuildFixedTabs();
        }

        // Or reopening this tab later resurrects a stale draft.
        drafts.Remove(tab.Id);
        if (gameTabId == tab.Id)
            gameTabId = null;
    }

    /// <summary>Drag-reorder: moves the dragged tab to whatever cell the cursor is over.</summary>
    private void HandleGameTabDrag(TabState tab, TabState[] visible)
    {
        if (!ImGui.IsItemActive() || !ImGui.IsMouseDragging(ImGuiMouseButton.Left, 4f))
            return;

        // Plain loops, not List.Find: this runs per hovered cell and the
        // predicates would capture (see IsFcOnlyTab for the same reasoning).
        var mouseX = ImGui.GetIO().MousePos.X;
        var targetIndex = -1;
        var currentIndex = -1;
        for (var i = 0; i < gameTabCells.Count; i++)
        {
            var cell = gameTabCells[i];
            if (mouseX >= cell.Min.X && mouseX < cell.Max.X)
                targetIndex = i;
            if (cell.Tab.Id == tab.Id)
                currentIndex = i;
        }

        if (targetIndex < 0 || currentIndex < 0 || targetIndex == currentIndex)
            return;

        gameOrderScratch.Clear();
        foreach (var other in visible)
            gameOrderScratch.Add(other.Id);

        gameOrderScratch.RemoveAt(currentIndex);
        gameOrderScratch.Insert(targetIndex, tab.Id);
        tabs.SetOrder(gameOrderScratch);
    }

    private void DrawGamePresenceDot(TabState tab, Vector2 cellMin, float height)
    {
        var color = PresenceColor(plugin.Presence.StatusFor(tab.TellPartner!));

        ImGui.GetWindowDrawList().AddCircleFilled(
            new Vector2(cellMin.X + 12f, cellMin.Y + height * 0.5f), 2.6f,
            ImGui.GetColorU32(color), 12);
    }

    private static void DrawGameUnreadCount(TabState tab, Vector2 cellMin, Vector2 cellMax)
    {
        var text = tab.Unread > 9 ? "9+" : tab.Unread.ToString();
        var radius = ImGui.GetFontSize() * 0.36f;
        var center = new Vector2(cellMax.X - radius - 2f, cellMin.Y + radius + 1f);

        ImGui.GetWindowDrawList().AddCircleFilled(
            center, radius, ImGui.GetColorU32(new Vector4(0.72f, 0.16f, 0.16f, 0.95f)), 12);

        var scale = 0.70f;
        var size = ImGui.CalcTextSize(text) * scale;
        ImGui.GetWindowDrawList().AddText(
            ImGui.GetFont(), ImGui.GetFontSize() * scale, center - size / 2f, 0xFFFFFFFF, text);
    }

    /// <summary>
    /// The round buttons beside the strip: new tab and settings, then lock and
    /// close on hover. Written out rather than passed glyph/click delegates —
    /// this runs every frame, and a lambda pair per button is a per-frame
    /// allocation for nothing (same reason the other themes' header buttons are
    /// four plain methods).
    /// </summary>
    private void DrawStripButtons(Vector2 origin, float size, float gap, float alpha)
    {
        var x = origin.X;
        var y = origin.Y;
        var step = size + gap;

        if (StripButton("##strip-add", new Vector2(x, y), size, alpha, 1f, "New tab", out var center, out var glyph))
            OpenNewTabPopup();
        VanillaChrome.PlusGlyph(center, size * 0.42f, glyph);

        x += step;
        if (StripButton("##strip-settings", new Vector2(x, y), size, alpha, 1f, "Settings", out center, out glyph))
            plugin.ToggleConfigUi();
        VanillaChrome.GearGlyph(center, size * 0.62f, glyph);

        if (controlsFade <= 0.01f)
            return;

        var locked = plugin.Configuration.LockWindow;
        x += step;
        if (StripButton(
                "##strip-lock", new Vector2(x, y), size, alpha, controlsFade,
                locked ? "Unlock window" : "Lock window (position and size)", out center, out glyph))
        {
            plugin.Configuration.LockWindow = !locked;
            plugin.Configuration.Save();
        }

        VanillaChrome.LockGlyph(center, size * 0.62f, glyph, locked);

        x += step;
        if (StripButton("##strip-close", new Vector2(x, y), size, alpha, controlsFade, "Close", out center, out glyph))
            IsOpen = false;
        VanillaChrome.CrossGlyph(center, size * 0.40f, glyph);
    }

    /// <summary>
    /// Submits one round strip button and draws its bevel, handing back where
    /// its glyph goes and what color it should be. True when clicked.
    /// </summary>
    private static bool StripButton(
        string id, Vector2 pos, float size, float alpha, float fade, string tooltip,
        out Vector2 center, out uint glyphColor)
    {
        ImGui.SetCursorScreenPos(pos);
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        center = pos + new Vector2(size * 0.5f, size * 0.5f);
        VanillaChrome.Circle(center, size * 0.5f - 0.5f, alpha * fade, hovered);

        var color = hovered ? VanillaChrome.Label : VanillaChrome.LabelDim;
        glyphColor = ImGui.GetColorU32(color with { W = color.W * fade });

        if (hovered)
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    private string newTabName = string.Empty;
    private readonly HashSet<XivChatType> newTabChannels = [];

    private const string NewTabPopup = "new-tab";

    /// <summary>"+" asks for a name and which channels the tab collects, as the game does.</summary>
    private void OpenNewTabPopup()
    {
        newTabName = SettingsWindow.UniqueTabName(plugin.Configuration);
        newTabChannels.Clear();
        newTabChannels.Add(XivChatType.Say);
        ImGui.OpenPopup(NewTabPopup);
    }

    private void DrawNewTabPopup()
    {
        using var popup = ImRaii.Popup(NewTabPopup);
        if (!popup.Success)
            return;

        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("Name", ref newTabName, 48);

        foreach (var (group, channels) in SettingsWindow.ChannelGroups)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
            {
                ImGui.TextUnformatted(group);
            }

            using var table = ImRaii.Table("##newtab-" + group, 3);
            if (!table.Success)
                continue;

            foreach (var (type, label) in channels)
            {
                ImGui.TableNextColumn();
                var enabled = newTabChannels.Contains(type);
                using var tint = ImRaii.PushColor(ImGuiCol.Text, ChatColors.For(type), enabled);
                if (!ImGui.Checkbox(label, ref enabled))
                    continue;

                if (enabled)
                    newTabChannels.Add(type);
                else
                    newTabChannels.Remove(type);
            }
        }

        ImGui.Separator();

        var name = newTabName.Trim();
        var valid = name.Length > 0
                    && newTabChannels.Count > 0
                    && !plugin.Configuration.Tabs.Exists(t => t.Name == name);

        using (ImRaii.Disabled(!valid))
        {
            if (ImGui.Button("Create"))
            {
                CreateTab(name);
                ImGui.CloseCurrentPopup();
            }
        }

        if (!valid && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(newTabChannels.Count == 0
                ? "Pick at least one channel."
                : "That name is taken.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void CreateTab(string name)
    {
        var config = plugin.Configuration;
        config.Tabs.Add(new TabConfig { Name = name, Channels = [.. newTabChannels] });
        config.Save();
        tabs.RebuildFixedTabs();

        // Select it: a tab you just described is the one you want to look at.
        gameTabId = "tab:" + name;
    }
}
