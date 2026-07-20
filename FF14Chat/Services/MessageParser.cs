using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FF14Chat.Model;

namespace FF14Chat.Services;

/// <summary>Turns a SeString into styled segments (colors, item/map/player links).</summary>
public static partial class MessageParser
{
    private static readonly Vector4 AutoTranslateColor = new(0.62f, 0.87f, 0.61f, 1f);

    public static List<MessageSegment> Parse(SeString message)
    {
        var segments = new List<MessageSegment>();
        var colors = new Stack<Vector4>();
        SegmentLink? link = null;

        foreach (var payload in message.Payloads)
        {
            switch (payload)
            {
                case UIForegroundPayload fg:
                    if (!fg.IsEnabled)
                    {
                        if (colors.Count > 0)
                            colors.Pop();
                    }
                    else
                    {
                        colors.Push(RgbaToVector(fg.RGBA));
                    }

                    break;

                case ItemPayload ip:
                    link = new SegmentLink.Item(ip.ItemId, ip.IsHQ, ResolveItemName(ip));
                    break;

                case MapLinkPayload mp:
                    link = new SegmentLink.Map(mp);
                    break;

                case PlayerPayload pp:
                    link = new SegmentLink.Player(FormatPlayer(pp));
                    break;

                // Link payloads end with a raw terminator payload.
                case RawPayload:
                    link = null;
                    break;

                case NewLinePayload:
                    segments.Add(new MessageSegment("\n", null, null));
                    break;

                case AutoTranslatePayload at:
                    segments.Add(new MessageSegment(at.Text, AutoTranslateColor, null));
                    break;

                case TextPayload { Text.Length: > 0 } tp:
                    segments.Add(new MessageSegment(
                        tp.Text,
                        colors.Count > 0 ? colors.Peek() : null,
                        link));
                    break;
            }
        }

        return ParseEmotes(LinkifyUrls(segments));
    }

    /// <summary>Splits plain text segments so known ":shortcode:" runs render as emotes.</summary>
    private static List<MessageSegment> ParseEmotes(List<MessageSegment> segments)
    {
        List<MessageSegment>? result = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var text = segment.Text;

            List<(int Start, int Length, string Emoji)>? hits = null;
            if (segment.Link == null && text.Length > 2)
            {
                var colon = text.IndexOf(':');
                while (colon >= 0 && colon < text.Length - 2)
                {
                    var close = text.IndexOf(':', colon + 1);
                    if (close < 0)
                        break;

                    if (close > colon + 1 && Emotes.TryGet(text[(colon + 1)..close], out var emoji))
                    {
                        (hits ??= []).Add((colon, close - colon + 1, emoji));
                        colon = text.IndexOf(':', close + 1);
                    }
                    else
                    {
                        // The closing colon may open the next emote (":x:sob:").
                        colon = close;
                    }
                }
            }

            if (hits == null)
            {
                result?.Add(segment);
                continue;
            }

            result ??= [.. segments.GetRange(0, i)];

            var consumed = 0;
            foreach (var (start, length, emoji) in hits)
            {
                if (start > consumed)
                    result.Add(segment with { Text = text[consumed..start] });
                result.Add(segment with { Text = text[start..(start + length)], Emote = emoji });
                consumed = start + length;
            }

            if (consumed < text.Length)
                result.Add(segment with { Text = text[consumed..] });
        }

        return result ?? segments;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Splits plain text segments so URLs become clickable link segments.</summary>
    private static List<MessageSegment> LinkifyUrls(List<MessageSegment> segments)
    {
        List<MessageSegment>? result = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var matches = segment.Link == null && segment.Text.Contains('.')
                ? UrlRegex().Matches(segment.Text)
                : null;
            if (matches is not { Count: > 0 })
            {
                result?.Add(segment);
                continue;
            }

            // First split: copy everything already passed through unchanged.
            result ??= [.. segments.GetRange(0, i)];

            var consumed = 0;
            foreach (Match match in matches)
            {
                if (match.Index > consumed)
                    result.Add(segment with { Text = segment.Text[consumed..match.Index] });

                var target = match.Value.StartsWith("www.", System.StringComparison.OrdinalIgnoreCase)
                    ? "https://" + match.Value
                    : match.Value;
                result.Add(segment with { Text = match.Value, Link = new SegmentLink.Url(target) });
                consumed = match.Index + match.Length;
            }

            if (consumed < segment.Text.Length)
                result.Add(segment with { Text = segment.Text[consumed..] });
        }

        return result ?? segments;
    }

    /// <summary>Finds the first player payload and formats it as "Name@World".</summary>
    public static string? ExtractPlayer(SeString text)
    {
        foreach (var payload in text.Payloads)
        {
            if (payload is PlayerPayload player)
                return FormatPlayer(player);
        }

        return null;
    }

    public static string FormatPlayer(PlayerPayload player)
    {
        var world = player.World.ValueNullable?.Name.ExtractText();
        return world is { Length: > 0 } ? $"{player.PlayerName}@{world}" : player.PlayerName;
    }

    private static string? ResolveItemName(ItemPayload ip) =>
        GameData.ItemName(ip.ItemId, ip.Kind == ItemKind.EventItem) ?? ip.DisplayName;

    private static Vector4 RgbaToVector(uint rgba) => new(
        ((rgba >> 24) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        ((rgba >> 8) & 0xFF) / 255f,
        1f);
}
