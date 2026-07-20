using System.Text;
using Dalamud.Game.Text.Sanitizer;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FF14Chat.Services;

/// <summary>
/// Sends text through the game's chat box entry point. This bypasses the
/// native input UI, so everything must be sanitized before it goes out.
/// </summary>
public static class ChatSender
{
    // The game's chat input caps at 500 UTF-8 bytes.
    public const int MaxBytes = 500;

    private static Sanitizer? sanitizer;

    /// <summary>Returns false if the text was rejected (too long); empty text is a no-op success.</summary>
    public static bool Send(string text)
    {
        var sanitized = Sanitize(text);
        if (sanitized.Length == 0)
            return true;

        if (Encoding.UTF8.GetByteCount(sanitized) > MaxBytes)
            return false;

        Plugin.Framework.RunOnFrameworkThread(() => SendInternal(sanitized));
        return true;
    }

    private static unsafe void SendInternal(string text)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var utf8 = Utf8String.FromString(text);
        try
        {
            uiModule->ProcessChatBoxEntry(utf8, 0, false);
        }
        finally
        {
            utf8->Dtor(true);
        }
    }

    private static string Sanitize(string text)
    {
        // Dalamud's Sanitizer applies the game's own string sanitation
        // (the pass the native input runs before sending); control
        // characters are stripped on top — a newline would smuggle a
        // second command past the single-line assumption.
        sanitizer ??= new Sanitizer(Plugin.ClientState.ClientLanguage);
        var gameClean = sanitizer.Sanitize(text);

        var builder = new StringBuilder(gameClean.Length);
        foreach (var ch in gameClean)
        {
            if (!char.IsControl(ch))
                builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}
