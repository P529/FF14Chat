using System.Text;
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
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsControl(ch))
                builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}
