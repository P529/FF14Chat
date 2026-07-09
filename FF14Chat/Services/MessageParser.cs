using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FF14Chat.Model;
using Lumina.Excel.Sheets;

namespace FF14Chat.Services;

/// <summary>Turns a SeString into styled segments (colors, item links).</summary>
public static class MessageParser
{
    private static readonly Vector4 AutoTranslateColor = new(0.62f, 0.87f, 0.61f, 1f);

    public static List<MessageSegment> Parse(SeString message)
    {
        var segments = new List<MessageSegment>();
        var colors = new Stack<Vector4>();
        ItemPayload? link = null;
        string? linkName = null;

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
                    link = ip;
                    linkName = ResolveItemName(ip);
                    break;

                // Link payloads end with a raw terminator payload.
                case RawPayload:
                    link = null;
                    linkName = null;
                    break;

                case NewLinePayload:
                    segments.Add(new MessageSegment("\n", null, null, false, null));
                    break;

                case AutoTranslatePayload at:
                    segments.Add(new MessageSegment(at.Text, AutoTranslateColor, null, false, null));
                    break;

                case TextPayload { Text.Length: > 0 } tp:
                    segments.Add(new MessageSegment(
                        tp.Text,
                        colors.Count > 0 ? colors.Peek() : null,
                        link?.ItemId,
                        link?.IsHQ ?? false,
                        linkName));
                    break;
            }
        }

        return segments;
    }

    private static string? ResolveItemName(ItemPayload ip)
    {
        if (ip.Kind == ItemKind.EventItem)
        {
            return Plugin.DataManager.GetExcelSheet<EventItem>().TryGetRow(ip.ItemId, out var eventItem)
                ? eventItem.Name.ExtractText()
                : ip.DisplayName;
        }

        return Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(ip.ItemId, out var item)
            ? item.Name.ExtractText()
            : ip.DisplayName;
    }

    private static Vector4 RgbaToVector(uint rgba) => new(
        ((rgba >> 24) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        ((rgba >> 8) & 0xFF) / 255f,
        1f);
}
