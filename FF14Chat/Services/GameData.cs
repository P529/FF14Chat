using Lumina.Excel.Sheets;

namespace FF14Chat.Services;

/// <summary>
/// Shared Excel-sheet lookups. These were previously duplicated across
/// PresenceTracker, PlayerActions, MessageParser and the UI.
/// </summary>
public static class GameData
{
    public static string WorldName(uint worldId) =>
        Plugin.DataManager.GetExcelSheet<World>().TryGetRow(worldId, out var world)
            ? world.Name.ExtractText()
            : string.Empty;

    public static ushort WorldIdByName(string world)
    {
        if (world.Length == 0)
            return 0;

        foreach (var row in Plugin.DataManager.GetExcelSheet<World>())
        {
            if (row.Name.ExtractText() == world)
                return (ushort)row.RowId;
        }

        return 0;
    }

    /// <summary>"Name@World", or just the name when the world is unknown.</summary>
    public static string WithWorld(string name, uint worldId)
    {
        var world = WorldName(worldId);
        return world.Length > 0 ? $"{name}@{world}" : name;
    }

    /// <summary>Item name by base id from the regular or event item sheet, null when the row is missing.</summary>
    public static string? ItemName(uint baseId, bool eventItem)
    {
        if (eventItem)
        {
            return Plugin.DataManager.GetExcelSheet<EventItem>().TryGetRow(baseId, out var row)
                ? row.Name.ExtractText()
                : null;
        }

        return Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(baseId, out var item)
            ? item.Name.ExtractText()
            : null;
    }
}
