using System.Collections.Generic;
using System.IO;
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

                case QuestPayload qp:
                    link = new SegmentLink.Quest(qp.Quest.RowId);
                    break;

                case StatusPayload sp:
                    link = new SegmentLink.Status(sp.Status.RowId);
                    break;

                case PartyFinderPayload pf:
                    link = new SegmentLink.PartyFinder(
                        pf.ListingId,
                        pf.LinkType == PartyFinderPayload.PartyFinderLinkType.PartyFinderNotification);
                    break;

                case DalamudLinkPayload dl:
                    link = new SegmentLink.Dalamud(dl);
                    break;

                case IconPayload icon:
                    segments.Add(new MessageSegment(
                        string.Empty,
                        colors.Count > 0 ? colors.Peek() : null,
                        null,
                        IconId: (uint)icon.Icon));
                    break;

                // Link payloads end with a raw terminator payload; achievement
                // and periodic-recruitment links instead START with an
                // unrecognized raw payload we decode here (the terminator, and
                // any other raw payload, falls through to null).
                case RawPayload rp:
                    if (rp.Data is { Length: > 5 } && rp.Data[1] == 0x27 && rp.Data[3] == 0x06)
                    {
                        using var reader = new BinaryReader(new MemoryStream(rp.Data, 4, rp.Data.Length - 4));
                        link = new SegmentLink.Achievement(GetInteger(reader));
                    }
                    else if (IsPeriodicRecruitment(rp.Data))
                    {
                        link = new SegmentLink.PartyFinder(0, Notification: true);
                    }
                    else
                    {
                        link = null;
                    }

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

                // Sentence punctuation glued to the URL ("see https://a.com.")
                // is prose, not link; a closing bracket only counts when the
                // URL itself opened one (wiki links like /wiki/Foo_(bar)).
                var url = match.Value;
                while (url.Length > 0 && (")]".Contains(url[^1])
                           ? url.IndexOf(url[^1] == ')' ? '(' : '[') < 0
                           : ".,;:!?".Contains(url[^1])))
                {
                    url = url[..^1];
                }

                var target = url.StartsWith("www.", System.StringComparison.OrdinalIgnoreCase)
                    ? "https://" + url
                    : url;
                result.Add(segment with { Text = url, Link = new SegmentLink.Url(target) });
                consumed = match.Index + url.Length;
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

    // The "N parties currently recruiting" notification arrives as this fixed
    // raw link payload (no id); clicking it just opens the Party Finder.
    private static readonly byte[] PeriodicRecruitment =
        [0x02, 0x27, 0x07, 0x08, 0x01, 0x01, 0x01, 0xFF, 0x01, 0x03];

    private static bool IsPeriodicRecruitment(byte[] data)
    {
        if (data.Length != PeriodicRecruitment.Length)
            return false;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != PeriodicRecruitment[i])
                return false;
        }

        return true;
    }

    /// <summary>Reads FFXIV's variable-length integer encoding (as used by link payload ids).</summary>
    private static uint GetInteger(BinaryReader input)
    {
        var num1 = (uint)input.ReadByte();
        if (num1 < 208U)
            return num1 - 1U;
        var num2 = (uint)((int)num1 + 1 & 15);
        var numArray = new byte[4];
        for (var index = 3; index >= 0; --index)
            numArray[index] = (num2 & 1 << index) == 0L ? (byte)0 : input.ReadByte();
        return System.BitConverter.ToUInt32(numArray, 0);
    }

    private static Vector4 RgbaToVector(uint rgba) => new(
        ((rgba >> 24) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        ((rgba >> 8) & 0xFF) / 255f,
        1f);
}
