using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace FF14Chat.Services;

/// <summary>
/// Command holds the full replacement input buffer; Display, when set, is the
/// shorter label shown in the popup (emote completions replace mid-sentence,
/// so their Command is the whole draft). Emote is an emoji drawn as an icon.
/// </summary>
public sealed record CommandEntry(
    string Command,
    string Description,
    bool FromPlugin,
    string? Display = null,
    string? Emote = null);

/// <summary>
/// Autocomplete source: every native text command (from the game's
/// TextCommand sheet, which includes emotes) plus registered plugin commands.
/// </summary>
public sealed class CommandIndex
{
    private List<CommandEntry>? nativeEntries;

    /// <summary>Prefix-matches commands, best matches first. Query must start with '/'.</summary>
    public List<CommandEntry> Query(string prefix, int limit = 10)
    {
        nativeEntries ??= BuildNative();

        // Plugin commands come and go with plugin loads, so scan them live.
        var pluginEntries = Plugin.CommandManager.Commands
            .Where(kv => kv.Key.Length > 1 && kv.Key[0] == '/')
            .Select(kv => new CommandEntry(kv.Key, kv.Value.HelpMessage, true));

        return nativeEntries.Concat(pluginEntries)
            .Where(e => e.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(e => e.Command, StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e.Command.Length)
            .ThenBy(e => e.Command, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static List<CommandEntry> BuildNative()
    {
        var result = new List<CommandEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Plugin.DataManager.GetExcelSheet<TextCommand>())
        {
            var description = row.Description.ExtractText().Replace('\n', ' ');
            foreach (var form in new[] { row.Command, row.ShortCommand, row.Alias, row.ShortAlias })
            {
                var command = form.ExtractText();
                if (command.Length > 1 && command[0] == '/' && seen.Add(command))
                    result.Add(new CommandEntry(command, description, false));
            }
        }

        return result;
    }
}
