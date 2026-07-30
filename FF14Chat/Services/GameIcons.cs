using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FF14Chat.Services;

/// <summary>
/// Reads "common/font/gfdata.gfd" — the atlas layout for the game's bitmap
/// font icons (gil, HQ, element and gamepad glyphs, etc.). Paired with the
/// fonticon texture at draw time. Ported from ChatTwo's IconUtil, itself from
/// Soreepeong's GfdFileView.
/// </summary>
public static unsafe class GameIcons
{
    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct GfdEntry
    {
        public ushort Id;
        public ushort Left;
        public ushort Top;
        public ushort Width;
        public ushort Height;
        public ushort Unk0A;
        public ushort Redirect;
        public ushort Unk0E;

        public bool IsEmpty => Width == 0 || Height == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GfdHeader
    {
        public fixed byte Signature[8];
        public int Count;
        public fixed byte Padding[4];
    }

    private static byte[]? file;

    private static bool TryGetEntries(out ReadOnlySpan<GfdEntry> entries)
    {
        file ??= Plugin.DataManager.GetFile("common/font/gfdata.gfd")?.Data;
        if (file == null || file.Length < sizeof(GfdHeader))
        {
            entries = default;
            return false;
        }

        var count = MemoryMarshal.AsRef<GfdHeader>(file).Count;
        var bytes = file.AsSpan(sizeof(GfdHeader));
        var span = MemoryMarshal.Cast<byte, GfdEntry>(bytes);
        entries = span.Length < count ? span : span[..count];
        return true;
    }

    /// <summary>Looks up an icon's atlas rect, following redirects.</summary>
    public static bool TryGetEntry(uint iconId, out GfdEntry entry) => TryGetEntry(iconId, 0, out entry);

    // Redirects are followed by recursion, and the table is game data we don't
    // control: a cycle would recurse until the stack runs out, which takes the
    // whole process with it (StackOverflowException can't be caught). Chat
    // icons need one hop, so a handful is already a broken table.
    private const int MaxRedirects = 4;

    private static bool TryGetEntry(uint iconId, int depth, out GfdEntry entry)
    {
        entry = default;
        if (iconId == 0 || depth > MaxRedirects || !TryGetEntries(out var entries))
            return false;

        // The table is dense and 1-indexed by Id for the icons chat uses, so a
        // direct index is enough; guard it and fall back to a scan otherwise.
        if (iconId <= (uint)entries.Length && entries[(int)(iconId - 1)].Id == iconId)
        {
            entry = entries[(int)(iconId - 1)];
        }
        else
        {
            var found = false;
            foreach (var candidate in entries)
            {
                if (candidate.Id == iconId)
                {
                    entry = candidate;
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        if (entry.Redirect != 0)
            return TryGetEntry(entry.Redirect, depth + 1, out entry);

        return !entry.IsEmpty;
    }
}
