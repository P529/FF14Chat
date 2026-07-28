using System;

namespace FF14Chat.Services.Translation;

/// <summary>
/// DeepL's target language set. The LLM providers speak the same codes; the
/// label is what gets handed to the model, since "PT-BR" alone is ambiguous.
/// </summary>
public static class Languages
{
    public static readonly (string Code, string Label)[] All =
    [
        ("BG", "Bulgarian"),
        ("CS", "Czech"),
        ("DA", "Danish"),
        ("DE", "German"),
        ("EL", "Greek"),
        ("EN-GB", "English (British)"),
        ("EN-US", "English (American)"),
        ("ES", "Spanish"),
        ("ET", "Estonian"),
        ("FI", "Finnish"),
        ("FR", "French"),
        ("HU", "Hungarian"),
        ("ID", "Indonesian"),
        ("IT", "Italian"),
        ("JA", "Japanese"),
        ("KO", "Korean"),
        ("LT", "Lithuanian"),
        ("LV", "Latvian"),
        ("NB", "Norwegian (Bokmal)"),
        ("NL", "Dutch"),
        ("PL", "Polish"),
        ("PT-BR", "Portuguese (Brazilian)"),
        ("PT-PT", "Portuguese (European)"),
        ("RO", "Romanian"),
        ("RU", "Russian"),
        ("SK", "Slovak"),
        ("SL", "Slovenian"),
        ("SV", "Swedish"),
        ("TR", "Turkish"),
        ("UK", "Ukrainian"),
        ("ZH", "Chinese"),
    ];

    /// <summary>
    /// The language part of a target code ("EN-US" → "EN"). Providers detect
    /// source languages at this granularity, so it is what comparisons use.
    /// </summary>
    public static string BaseCode(string code)
    {
        var dash = code.IndexOf('-');
        return dash < 0 ? code : code[..dash];
    }

    /// <summary>Human-readable name, or the code itself when unrecognised.</summary>
    public static string Label(string code)
    {
        foreach (var (target, label) in All)
        {
            if (string.Equals(target, code, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        return code;
    }
}
