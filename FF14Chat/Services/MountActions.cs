using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;

namespace FF14Chat.Services;

/// <summary>
/// Mount lookups for /mountid. Dalamud already surfaces the ridden mount, so
/// this is sheet work only: turning a mount row into something worth printing.
/// </summary>
public static class MountActions
{
    /// <summary>Action row id an ItemAction points at when the item teaches a mount.</summary>
    private const uint MountItemActionType = 1322;

    // Mount row id -> the whistle/card that teaches it. The Item sheet is
    // ~45k rows, so this is built once and never per command.
    private static Dictionary<ushort, uint>? teachingItems;

    /// <summary>The mount the character is riding, 0 when on foot.</summary>
    public static ushort MountId(ICharacter character) => (ushort)(character.CurrentMount?.RowId ?? 0);

    /// <summary>
    /// Sheet name for a mount, or null when the row is missing or unnamed.
    /// Singular is stored lowercase for common nouns ("company chocobo")
    /// because the game capitalizes at render time via text macros, so the
    /// first letter is fixed up here.
    /// </summary>
    public static string? MountName(ushort mountId)
    {
        if (!Plugin.DataManager.GetExcelSheet<Mount>().TryGetRow(mountId, out var row))
            return null;

        var name = row.Singular.ExtractText();
        if (name.Length == 0)
            return null;

        return char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    /// <summary>
    /// Item that teaches this mount, 0 when none exists — quest and event
    /// mounts (the Company Chocobo above all) are unlocked server-side without
    /// ever handing over an item.
    /// </summary>
    public static uint TeachingItemId(ushort mountId)
    {
        teachingItems ??= BuildTeachingItems();
        return teachingItems.GetValueOrDefault(mountId);
    }

    private static Dictionary<ushort, uint> BuildTeachingItems()
    {
        // The Mount sheet has no back-reference to the item that teaches it,
        // hence this reverse index.
        var map = new Dictionary<ushort, uint>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
        {
            if (item.ItemAction.RowId == 0)
                continue;

            var action = item.ItemAction.Value;
            if (action.Action.RowId != MountItemActionType)
                continue;

            var mountId = action.Data[0];
            if (mountId != 0)
                map.TryAdd(mountId, item.RowId); // TryAdd: keep the lowest row id
        }

        return map;
    }
}
