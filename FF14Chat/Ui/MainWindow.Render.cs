using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Hooking;
using FF14Chat.Model;
using FF14Chat.Services;
using FF14Chat.Services.Translation;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;


namespace FF14Chat.Ui;

/// <summary>
/// Turning a message into pixels: the per-line layout, segment and token
/// drawing with its manual wrapping, inline emote/icon/job art, translated
/// bodies and their tooltips, and the tokenizer the wrapping runs on.
/// </summary>
public partial class MainWindow
{
    private void DrawMessage(Message message, int repeats = 1)
    {
        // Display strings are cached on the message: this runs for up to
        // MaxRenderedMessages rows every frame and all inputs are immutable
        // (the clock format tags its cache and rebuilds on change).
        var use24h = plugin.Configuration.Use24HourClock;
        if (message.StampCache == null || message.StampCache24h != use24h)
        {
            message.StampCache24h = use24h;
            message.StampCache = use24h
                ? $"[{message.Timestamp:HH:mm}]"
                : $"[{message.Timestamp:h:mm tt}]";
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
            ImGui.TextUnformatted(message.StampCache);

        // Masked: flagged variants (source bits in the high byte) must color
        // and prefix like their base kind â€” Route already matches them into
        // the same tabs.
        var channelColor = ChatColors.For(ChatTypes.Mask(message.Type));

        if (!message.HasPrefixCache)
        {
            message.HasPrefixCache = true;
            message.PrefixCache = BuildPrefixSegments(message);
        }

        if (message.PrefixCache is { Count: > 0 } prefixSegments)
        {
            var prefixColor = channelColor;
            if (message.SenderJob is { } job)
            {
                if (plugin.Configuration.JobIconPartyNames)
                    DrawJobIcon(job);
                if (plugin.Configuration.RoleColorPartyNames && RoleColor(job) is { } roleColor)
                    prefixColor = roleColor;
            }

            foreach (var segment in prefixSegments)
            {
                // Icon segments (the cross-world glyph) have empty text; draw
                // the glyph, or skip it if the texture isn't ready this frame.
                if (segment.IconId != 0)
                {
                    DrawIconToken(segment);
                    continue;
                }

                DrawSegment(segment, segment.Color ?? prefixColor);
            }
        }

        // Read once per draw: the field is published by the translation worker
        // and can change between two reads inside the same frame.
        var translation = message.Translation;

        if (translation is { Status: TranslationStatus.Done, Text: { } translated })
        {
            // The translated body carries no links, emotes or game colors â€” it
            // replaces the segments as one flat run in the translation color.
            // Everything before it (timestamp, job icon, prefix) is untouched.
            var body = TranslatedBodySegment(message, translation, translated);
            var color = TranslationService.TranslationColor(plugin.Configuration);
            if (DrawTranslatedBody(body, color) && plugin.Configuration.ShowTranslationTooltip)
                pendingTranslationTooltip = (message, translation);
        }
        else if (message.Segments.Count > 0)
        {
            foreach (var segment in message.Segments)
            {
                // Icon segments have empty text: draw the glyph, or skip the
                // segment entirely if the texture isn't ready this frame.
                if (segment.IconId != 0)
                {
                    DrawIconToken(segment);
                    continue;
                }

                if (segment.Emote != null && plugin.Configuration.RenderEmotes
                    && DrawEmoteToken(segment))
                    continue;

                // Item/map links stand out even when the game didn't color
                // them itself, so they read as clickable.
                var fallback = segment.Link is SegmentLink.Item or SegmentLink.Map or SegmentLink.Url or SegmentLink.Achievement or SegmentLink.PartyFinder or SegmentLink.Quest or SegmentLink.Dalamud
                    ? ChatColors.Link
                    : channelColor;
                DrawSegment(segment, segment.Color ?? fallback);
            }
        }
        else
        {
            DrawSegment(message.FallbackCache ??= new MessageSegment(message.Text, null, null), channelColor);
        }

        if (repeats > 1)
            DrawSegmentText($" Ã—{repeats}", ChatColors.Timestamp, null);
    }

    // Hovered translated body waiting for its tooltip, deferred to the end of
    // the log: submitting a tooltip window overwrites ImGui's last-item rect,
    // which the row still needs for the Ã—N counter and the mention highlight.
    // At most one row is hovered, so a single slot is enough.
    private (Message Message, TranslationState Translation)? pendingTranslationTooltip;

    // Render cache for translated bodies. It lives here rather than on Message
    // (which owns the caches derived from its own immutable fields): the entry
    // is keyed on the TranslationState instance so a re-translation rebuilds
    // it, and the weak table drops it when the message is evicted.
    private readonly ConditionalWeakTable<Message, TranslatedBody> translatedBodies = new();

    private sealed class TranslatedBody
    {
        public TranslationState? Source;
        public MessageSegment? Segment;
    }

    /// <summary>
    /// The translated body as a segment, so it word-wraps through the same
    /// token machinery as normal text. Built once per translation â€” this runs
    /// for every visible translated row, every frame.
    /// </summary>
    private MessageSegment TranslatedBodySegment(Message message, TranslationState translation, string text)
    {
        var cache = translatedBodies.GetOrCreateValue(message);

        // Identity, not record equality: `!=` on a record is a value compare
        // that would run for every visible translated row every frame, and a
        // fresh state instance is exactly what "the translation changed" means.
        if (!ReferenceEquals(cache.Source, translation))
        {
            cache.Source = translation;
            cache.Segment = new MessageSegment(text, null, null);
        }

        return cache.Segment!;
    }

    /// <summary>
    /// Draws a translated body as one flat run, reporting whether any of its
    /// word tokens is hovered. Hover is accumulated here instead of inside
    /// DrawToken, which owns the link hover handling normal segments depend on:
    /// the body is many ImGui items but must show a single tooltip.
    /// </summary>
    private bool DrawTranslatedBody(MessageSegment segment, Vector4 color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var hovered = false;
        var forceNewLine = false;
        foreach (var token in segment.Tokens ??= BuildTokens(segment.Text))
        {
            if (token == "\n")
            {
                forceNewLine = true;
                continue;
            }

            // No link, so DrawToken returns before its own hover test â€” this
            // stays the only IsItemHovered call for the token.
            DrawToken(token, null, forceNewLine);
            hovered |= ImGui.IsItemHovered();
            forceNewLine = false;
        }

        return hovered;
    }

    /// <summary>
    /// Hover tooltip for a translated body: the original line under a dim
    /// "JA â†’ Japanese" header, or just "original" when either end is unknown.
    /// </summary>
    private static void DrawTranslationTooltip(Message message, TranslationState translation)
    {
        using var tooltip = ImRaii.Tooltip();

        var header = translation.DetectedSource is { Length: > 0 } source
                     && translation.TargetLanguage is { Length: > 0 } target
            ? $"{source} â†’ {Languages.Label(target)}"
            : "original";

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted(header);
        }

        // Chat lines run long; unwrapped, the tooltip stretches off-screen.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 400);
        ImGui.TextUnformatted(message.Text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Inline Twemoji image at text height, continuing the line like a word
    /// token. False while the texture isn't ready (pending GPU upload, or it
    /// failed to load), so the ":shortcode:" text draws instead.
    /// </summary>
    private static bool DrawEmoteToken(MessageSegment segment)
    {
        var wrap = Emotes.GetTexture(segment.Emote!);
        if (wrap == null)
            return false;

        var size = ImGui.GetTextLineHeight();
        ContinueLineIfFits(size);
        ImGui.Image(wrap.Handle, new Vector2(size, size));

        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.Image(wrap.Handle, new Vector2(size * 3, size * 3));
            ImGui.TextUnformatted(segment.Text);
        }

        return true;
    }

    /// <summary>
    /// Inline game bitmap-font icon (gil, HQ, element glyphs, etc.) at text
    /// height, continuing the line like a word token. False while the atlas
    /// entry or texture isn't ready; the icon has no text, so nothing draws.
    /// </summary>
    private static bool DrawIconToken(MessageSegment segment)
    {
        if (!GameIcons.TryGetEntry(segment.IconId, out var entry))
            return false;

        var tex = Plugin.TextureProvider.GetFromGame("common/font/fonticon_ps5.tex").GetWrapOrDefault();
        if (tex == null)
            return false;

        var texSize = new Vector2(tex.Width, tex.Height);
        var lineHeight = ImGui.GetTextLineHeight();
        var ratio = lineHeight / entry.Height;
        var size = new Vector2(entry.Width, entry.Height) * ratio;

        // fonticon_ps5.tex is the hi-res (2x) sheet; the +170 row offset and the
        // *2 scaling on the atlas rect are specific to it (per ChatTwo).
        var uv0 = new Vector2(entry.Left, entry.Top + 170) * 2 / texSize;
        var uv1 = new Vector2(entry.Left + entry.Width, entry.Top + entry.Height + 170) * 2 / texSize;

        ContinueLineIfFits(size.X);
        ImGui.Image(tex.Handle, size, uv0, uv1);
        return true;
    }

    /// <summary>Framed job icon (62100 block) at text height, continuing the line.</summary>
    private static void DrawJobIcon(uint jobId)
    {
        var wrap = Plugin.TextureProvider
            .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(62100 + jobId))
            .GetWrapOrDefault();
        if (wrap == null)
            return;

        ImGui.SameLine(0, 4);
        var size = ImGui.GetTextLineHeight();
        ImGui.Image(wrap.Handle, new Vector2(size, size));
    }

    /// <summary>ClassJob.Role: 1 tank, 2 melee, 3 ranged, 4 healer, 0 hand/land.</summary>
    private static Vector4? RoleColor(uint jobId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(jobId, out var row))
            return null;

        return row.Role switch
        {
            1 => new Vector4(0.25f, 0.55f, 0.95f, 1f),
            4 => new Vector4(0.35f, 0.80f, 0.42f, 1f),
            2 or 3 => new Vector4(0.93f, 0.35f, 0.35f, 1f),
            _ => null,
        };
    }

    /// <summary>
    /// Draws a segment continuing the current chat line, word-wrapping
    /// against the window edge; tokens come from the segment's cache.
    /// Assumes the previous ImGui item is the preceding chunk of this same
    /// line (the timestamp starts every line).
    /// </summary>
    private void DrawSegment(MessageSegment segment, Vector4 color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var forceNewLine = false;
        foreach (var token in segment.Tokens ??= BuildTokens(segment.Text))
        {
            if (token == "\n")
            {
                forceNewLine = true;
                continue;
            }

            DrawToken(token, segment.Link, forceNewLine);
            forceNewLine = false;
        }
    }

    /// <summary>
    /// Uncached variant of <see cref="DrawSegment"/> for one-off strings
    /// (the Ã—N repeat counter).
    /// </summary>
    private void DrawSegmentText(string text, Vector4 color, SegmentLink? link)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);

        var forceNewLine = false;
        foreach (var token in BuildTokens(text))
        {
            if (token == "\n")
            {
                forceNewLine = true;
                continue;
            }

            DrawToken(token, link, forceNewLine);
            forceNewLine = false;
        }
    }

    /// <summary>SameLine-continues the current chat line if the width fits before the window edge.</summary>
    private static void ContinueLineIfFits(float width)
    {
        var lastEnd = ImGui.GetItemRectMax().X;
        var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (lastEnd + width <= rightEdge)
            ImGui.SameLine(0, 0);
    }

    private void DrawToken(string token, SegmentLink? link, bool forceNewLine)
    {
        if (!forceNewLine)
            ContinueLineIfFits(ImGui.CalcTextSize(token).X);

        ImGui.TextUnformatted(token);

        if (link == null || !ImGui.IsItemHovered())
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        switch (link)
        {
            case SegmentLink.Item item:
                if (plugin.Configuration.NativeItemTooltips)
                {
                    var rawId = item.ItemId + (item.Hq ? 1_000_000u : 0u);
                    nativeTooltipGrace = 2;
                    if (nativeTooltipItem != rawId && NativeItemTooltip.Open(rawId))
                        nativeTooltipItem = rawId;
                    if (nativeTooltipItem == rawId)
                        NativeItemTooltip.Reposition(mainWindowPos, mainWindowSize);
                    else
                        DrawItemTooltip(item);
                }
                else
                {
                    DrawItemTooltip(item);
                }

                if (clicked || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    contextItem = item;
                    linkClaimedRightClick |= ImGui.IsMouseClicked(ImGuiMouseButton.Right);
                    ImGui.OpenPopup(itemContextPopupId);
                }

                break;

            case SegmentLink.Map map:
                ImGui.SetTooltip("Click: open map");
                if (clicked)
                    Plugin.GameGui.OpenMapWithMapLink(map.Payload);
                break;

            case SegmentLink.Url url:
                ImGui.SetTooltip($"{url.Target}\nClick: open in browser");
                if (clicked)
                    Dalamud.Utility.Util.OpenLink(url.Target);
                break;

            case SegmentLink.Achievement achievement:
                ImGui.SetTooltip("Click: open achievement");
                if (clicked)
                    AchievementActions.Open(achievement.Id);
                break;

            case SegmentLink.PartyFinder partyFinder:
                ImGui.SetTooltip("Click: open Party Finder");
                if (clicked)
                {
                    if (partyFinder.Notification)
                        PartyFinderActions.OpenWindow();
                    else
                        PartyFinderActions.OpenListing(partyFinder.ListingId);
                }

                break;

            case SegmentLink.Quest quest:
                ImGui.SetTooltip("Click: open in journal");
                if (clicked)
                    QuestActions.Open(quest.QuestRowId);
                break;

            case SegmentLink.Status status:
                ImGui.SetTooltip(StatusName(status.StatusId));
                break;

            case SegmentLink.Dalamud dalamud:
                ImGui.SetTooltip("Click: follow link");
                if (clicked)
                    ChatLinkActions.Invoke(dalamud.Payload);
                break;

            case SegmentLink.Player player:
                ImGui.SetTooltip($"{player.Partner}\nClick: open tell tab â€” right-click: menu");
                if (clicked)
                {
                    var tellTab = tabs.OpenTellTab(player.Partner);
                    selectTabId = tellTab.Id;
                    focusInput = true;
                }

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    contextPartner = player.Partner;
                    linkClaimedRightClick = true;
                    ImGui.OpenPopup(logContextPopupId);
                }

                break;
        }
    }

    /// <summary>Status effect name from Lumina, falling back to an id string.</summary>
    private static string StatusName(uint statusId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(statusId, out var row)
        && row.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"Status #{statusId}";

    private static Vector4 RarityColor(byte rarity) => rarity switch
    {
        2 => new Vector4(0.55f, 0.95f, 0.55f, 1f), // green
        3 => new Vector4(0.45f, 0.65f, 1.00f, 1f), // blue
        4 => new Vector4(0.75f, 0.55f, 0.95f, 1f), // purple
        7 => new Vector4(0.95f, 0.60f, 0.75f, 1f), // pink (relic)
        _ => new Vector4(0.95f, 0.95f, 0.95f, 1f),
    };

    private static void DrawItemTooltip(SegmentLink.Item link)
    {
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(link.ItemId, out var item))
        {
            ImGui.SetTooltip($"{link.Name ?? $"Item #{link.ItemId}"}\nClick: copy name");
            return;
        }

        using var tooltip = ImRaii.Tooltip();

        var icon = Plugin.TextureProvider
            .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.Icon, link.Hq))
            .GetWrapOrEmpty();
        ImGui.Image(icon.Handle, new Vector2(40, 40));
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            using (ImRaii.PushColor(ImGuiCol.Text, RarityColor(item.Rarity)))
            {
                ImGui.TextUnformatted($"{item.Name.ExtractText()}{(link.Hq ? " " : "")}");
            }

            var category = item.ItemUICategory.ValueNullable?.Name.ExtractText() ?? "";
            using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
            {
                ImGui.TextUnformatted($"{category}  Â·  Item Level {item.LevelItem.RowId}");
                if (item.LevelEquip > 1)
                    ImGui.TextUnformatted($"Equip Level {item.LevelEquip}");
            }
        }

        var description = item.Description.ExtractText();
        if (description.Length > 0)
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 320);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
        }

        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Timestamp))
        {
            ImGui.TextUnformatted("Click: copy name");
        }
    }

    private static void Notify(string content)
    {
        Plugin.Notifications.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
        {
            Content = content,
            Minimized = true,
        });
    }

    /// <summary>
    /// Token list for a piece of chat text: words keeping their trailing
    /// spaces, with "\n" entries marking forced line breaks.
    /// </summary>
    private static string[] BuildTokens(string text)
    {
        List<string> tokens = [];
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            foreach (var token in Tokenize(newline < 0 ? text[start..] : text[start..newline]))
                tokens.Add(token);

            if (newline < 0)
                break;
            tokens.Add("\n");
            start = newline + 1;
        }

        return [.. tokens];
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

    /// <summary>Opening/closing affixes around the sender name per channel, or null for no prefix.</summary>
    private static (string Open, string Close)? PrefixDecoration(Message message)
    {
        if (message.Sender.Length == 0)
            return null;

        // Masked for the same reason as the channel color lookup.
        return ChatTypes.Mask(message.Type) switch
        {
            XivChatType.TellIncoming => ("", " >>"),
            XivChatType.TellOutgoing => (">> ", ":"),
            XivChatType.Party or XivChatType.CrossParty => ("(", ")"),
            XivChatType.Alliance => ("((", "))"),
            // Emote messages already contain the player's name in the text.
            XivChatType.CustomEmote or XivChatType.StandardEmote => null,
            XivChatType.FreeCompany => ("[FC]<", ">"),
            _ => ("", ":"),
        };
    }

    /// <summary>
    /// Builds the sender prefix as styled segments. The sender is parsed from
    /// its raw SeString (not the flattened text) so a cross-world player's
    /// world icon survives and their name stays separated from the world it
    /// precedes. Returns null for channels that carry no prefix.
    /// </summary>
    private static IReadOnlyList<MessageSegment>? BuildPrefixSegments(Message message)
    {
        if (PrefixDecoration(message) is not { } decoration)
            return null;

        var (open, close) = decoration;
        var link = message.SenderPlayer != null ? new SegmentLink.Player(message.SenderPlayer) : null;
        var segments = new List<MessageSegment>();

        if (open.Length > 0)
            segments.Add(new MessageSegment(open, null, link));

        // Parsed sender segments: text runs inherit the player link so the
        // whole prefix is one click target; icon segments (the cross-world
        // glyph) pass through so DrawIconToken renders them.
        var sender = Dalamud.Game.Text.SeStringHandling.SeString.Parse(message.SenderRaw);
        foreach (var segment in MessageParser.Parse(sender))
            segments.Add(segment.Link == null ? segment with { Link = link } : segment);

        segments.Add(new MessageSegment(close + " ", null, link));
        return segments;
    }
}
