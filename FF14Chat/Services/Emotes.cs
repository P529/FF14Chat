using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Dalamud.Interface.Textures.TextureWraps;

namespace FF14Chat.Services;

/// <summary>
/// Discord-style emote shortcodes (":sob:") rendered as Twemoji images.
/// Everything ships inside the DLL: the shortcode → emoji map (gemoji
/// aliases, the same names Discord uses) and a zip of the Twemoji 72x72
/// PNGs it references. Textures are created lazily per emote and owned
/// here, so <see cref="Dispose"/> must run on plugin unload.
/// </summary>
public static class Emotes
{
    private static readonly Dictionary<string, string> ByName = LoadMap();
    private static readonly List<string> Names = [.. ByName.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    private static readonly object Sync = new();

    /// <summary>Emoji → texture; a null value means loading or failed (text fallback).</summary>
    private static readonly Dictionary<string, IDalamudTextureWrap?> Textures = [];

    private static ZipArchive? archive;
    private static bool archiveLoadFailed;
    private static bool disposed;

    public static bool TryGet(string shortcode, out string emoji) =>
        ByName.TryGetValue(shortcode, out emoji!);

    /// <summary>
    /// Texture for an emoji, or null while the GPU upload is pending (or
    /// after a failure); callers fall back to drawing the shortcode text.
    /// Draw-thread only.
    /// </summary>
    public static IDalamudTextureWrap? GetTexture(string emoji)
    {
        byte[] bytes;
        lock (Sync)
        {
            if (disposed)
                return null;
            if (Textures.TryGetValue(emoji, out var wrap))
                return wrap;

            // First request: mark as in-flight, read the PNG from the
            // embedded zip (a few KB, cheap enough for the draw thread).
            // A failed read leaves the null marker → permanent text fallback,
            // same as every other failure mode here.
            Textures[emoji] = null;
            try
            {
                var entry = Archive?.GetEntry(FileName(emoji) + ".png");
                if (entry == null)
                    return null;

                using var stream = entry.Open();
                using var buffer = new MemoryStream((int)entry.Length);
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, $"Emote zip read failed for {emoji}");
                return null;
            }
        }

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var wrap = await Plugin.TextureProvider.CreateFromImageAsync(bytes).ConfigureAwait(false);
                lock (Sync)
                {
                    if (disposed)
                    {
                        wrap.Dispose();
                        return;
                    }

                    Textures[emoji] = wrap;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Warning(e, $"Emote texture upload failed for {emoji}");
            }
        });

        return null;
    }

    /// <summary>Shortcode completion: prefix matches first, then substring.</summary>
    public static List<(string Name, string Emoji)> Query(string partial, int limit)
    {
        var result = new List<(string, string)>(limit);
        foreach (var name in Names)
        {
            if (name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((name, ByName[name]));
                if (result.Count == limit)
                    return result;
            }
        }

        foreach (var name in Names)
        {
            if (!name.StartsWith(partial, StringComparison.OrdinalIgnoreCase)
                && name.Contains(partial, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((name, ByName[name]));
                if (result.Count == limit)
                    break;
            }
        }

        return result;
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            disposed = true;
            foreach (var wrap in Textures.Values)
                wrap?.Dispose();
            Textures.Clear();
            archive?.Dispose();
            archive = null;
        }
    }

    /// <summary>Lazily opened over the embedded zip; caller holds Sync.</summary>
    private static ZipArchive? Archive
    {
        get
        {
            if (archive != null || archiveLoadFailed)
                return archive;

            try
            {
                using var resource = typeof(Emotes).Assembly
                    .GetManifestResourceStream("FF14Chat.Data.emotes.zip")
                    ?? throw new InvalidOperationException("emotes.zip resource missing");

                // Copied out because the archive random-accesses entries and
                // outlives this call; ~1.7 MB resident is the whole cost.
                var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            }
            catch (Exception e)
            {
                archiveLoadFailed = true;
                Plugin.Log.Error(e, "Failed to open embedded emote archive");
            }

            return archive;
        }
    }

    private const char ZwjChar = (char)0x200D;
    private const string Vs16String = "\uFE0F";

    /// <summary>
    /// Twemoji asset naming: hex codepoints joined by '-'; U+FE0F variant
    /// selectors are dropped unless the sequence contains a ZWJ.
    /// </summary>
    private static string FileName(string emoji)
    {
        var s = emoji.Contains(ZwjChar) ? emoji : emoji.Replace(Vs16String, string.Empty);
        return string.Join('-', s.EnumerateRunes().Select(r => r.Value.ToString("x")));
    }

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            using var stream = typeof(Emotes).Assembly
                .GetManifestResourceStream("FF14Chat.Data.emotes.json");
            if (stream == null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var map = Newtonsoft.Json.JsonConvert
                .DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            return map == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load emote shortcode map");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
