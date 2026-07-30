using System;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using FF14Chat.Services.Translation;

namespace FF14Chat.Ui;

public partial class SettingsWindow
{
    // Indexed by TranslationProviderKind, so order is fixed by the enum.
    private static readonly string[] ProviderLabels =
        ["DeepL (API key)", "Anthropic (Claude)", "OpenAI-compatible", "Google / Bing / Yandex (no key)"];

    private static readonly string[] LanguageLabels = Array.ConvertAll(Languages.All, l => l.Label);

    private static readonly int DefaultTargetIndex = LanguageIndexOrZero("EN-US");
    private static readonly int DefaultOutgoingIndex = LanguageIndexOrZero("JA");

    /// <summary>In-flight connection test. The render thread polls it; waiting
    /// on it would stall every frame behind a network round trip.</summary>
    private Task<string>? translationTest;
    private string translationTestResult = string.Empty;

    private void DrawTranslateTab(Configuration config)
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.GoldBright))
        {
            ImGui.TextWrapped(
                "While this is on, message text is sent to a third-party translation API. "
                + "Your API key is stored as plain text in the Dalamud plugin config file.");
        }

        SectionHeader("Translation", first: true);

        ConsentToggle(config, "Translate incoming messages",
            config.TranslateIncoming, static (c, v) => c.TranslateIncoming = v);
        ConsentToggle(config, "Translate my outgoing messages",
            config.TranslateOutgoing, static (c, v) => c.TranslateOutgoing = v,
            "What you type is translated before it is sent.\nText starting with / is never translated, so commands can't break.");

        DrawConsentPopup(config);

        var provider = config.TranslationProvider;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Provider##tr-provider", ref provider, ProviderLabels, ProviderLabels.Length))
        {
            config.TranslationProvider = provider;
            config.Save();
            plugin.Translation.InvalidateProvider();
        }

        DrawApiSection(config);
        DrawLanguageSection(config);
        DrawAppearanceSection(config);
        DrawFilterSection(config);
        DrawUsageSection();
    }

    private const string ConsentPopup = "Send chat to a translation service?##tr-consent";

    /// <summary>The switch-on waiting on the confirmation, null when none is.</summary>
    private Action<Configuration, bool>? pendingConsent;

    /// <summary>
    /// Enable toggle that asks first. The default provider needs no API key, so
    /// nothing else stands between ticking a box and chat text leaving the
    /// machine — this makes that a decision rather than a stray click. Asked
    /// once; switching off, or on again later, goes straight through.
    /// </summary>
    private void ConsentToggle(
        Configuration config, string label, bool value,
        Action<Configuration, bool> apply, string? tooltip = null)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            if (value && !config.TranslationConsent)
            {
                pendingConsent = apply;
                ImGui.OpenPopup(ConsentPopup);
            }
            else
            {
                apply(config, value);
                config.Save();
            }
        }

        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawConsentPopup(Configuration config)
    {
        using var popup = ImRaii.PopupModal(ConsentPopup, ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup.Success)
            return;

        ImGui.TextWrapped(
            "Chat you receive — and, with outgoing translation on, what you type — will be sent "
            + "to the translation service selected below. Tells and private conversations included.");

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.TextWrapped(
                "The default service needs no account, which also means nothing on their end ties the "
                + "text to you beyond your IP address. Choosing a provider with an API key instead "
                + "sends it under that account.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Enable##tr-consent-ok", new System.Numerics.Vector2(120f, 0f)))
        {
            config.TranslationConsent = true;
            pendingConsent?.Invoke(config, true);
            pendingConsent = null;
            config.Save();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##tr-consent-cancel", new System.Numerics.Vector2(120f, 0f)))
        {
            // The checkbox was never applied, so cancelling needs no undo.
            pendingConsent = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawApiSection(Configuration config)
    {
        SectionHeader("API");

        // default also catches a provider id a newer build wrote and this one
        // doesn't know, which would otherwise render an empty API section.
        switch ((TranslationProviderKind)config.TranslationProvider)
        {
            case TranslationProviderKind.MachineTranslate:
            default:
                DimWrapped(
                    "No account needed: the free web endpoints behind Google, Bing and Yandex translate, "
                    + "tried in that order until one answers. Roughest quality of the four, and the most "
                    + "likely to rate limit a busy channel.");
                break;

            case TranslationProviderKind.DeepL:
                ApiField(config, "API key##tr-deepl-key", "DeepL API key",
                    config.DeepLApiKey, static (c, v) => c.DeepLApiKey = v, masked: true);
                DimWrapped("Free keys end in \":fx\" and are detected automatically.");
                break;

            case TranslationProviderKind.Anthropic:
                ApiField(config, "API key##tr-llm-key", "Anthropic API key",
                    config.LlmApiKey, static (c, v) => c.LlmApiKey = v, masked: true);
                ApiField(config, "Model##tr-llm-model", "claude-haiku-4-5-20251001",
                    config.LlmModel, static (c, v) => c.LlmModel = v);
                break;

            case TranslationProviderKind.OpenAiCompatible:
                ApiField(config, "API key##tr-llm-key", "API key",
                    config.LlmApiKey, static (c, v) => c.LlmApiKey = v, masked: true);
                ApiField(config, "Model##tr-llm-model", "gpt-4o-mini",
                    config.LlmModel, static (c, v) => c.LlmModel = v);
                ApiField(config, "Base URL##tr-llm-url", "https://api.openai.com/v1",
                    config.LlmBaseUrl, static (c, v) => c.LlmBaseUrl = v);
                DimWrapped("Anything speaking the OpenAI chat API: Ollama, LM Studio, OpenRouter — point the base URL at it.");
                break;
        }

        ImGui.Spacing();
        DrawConnectionTest();
        DrawFallbackNotice(config);
        DrawPausedNotice();
    }

    /// <summary>
    /// Key/model/URL field. The value is written back per keystroke so it
    /// survives the next frame, but only committed on focus loss — saving the
    /// config and rebuilding the provider per character would be wasteful and
    /// would fire against half-typed keys.
    /// </summary>
    private void ApiField(
        Configuration config, string label, string hint, string value,
        Action<Configuration, string> apply, bool masked = false)
    {
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputTextWithHint(label, hint, ref value, 512,
                masked ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None))
        {
            apply(config, value);
        }

        if (!ImGui.IsItemDeactivatedAfterEdit())
            return;

        // Pasted keys routinely carry a trailing newline or space.
        apply(config, value.Trim());
        config.Save();
        plugin.Translation.InvalidateProvider();
    }

    private void DrawConnectionTest()
    {
        if (translationTest is { IsCompleted: true } finished)
        {
            // TestAsync is documented not to throw; if it ever does, report it
            // rather than letting Result rethrow on the render thread.
            translationTestResult = finished.IsCompletedSuccessfully
                ? finished.Result
                : finished.Exception?.GetBaseException().Message ?? "Test failed.";
            translationTest = null;
        }

        var running = translationTest != null;
        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button("Test##tr-test"))
            {
                translationTestResult = string.Empty;
                translationTest = plugin.Translation.TestAsync();
                running = true;
            }
        }

        if (running)
        {
            ImGui.SameLine();
            DimWrapped("Testing…");
        }
        else if (translationTestResult.Length > 0)
        {
            DimWrapped(translationTestResult);
        }
    }

    private void DrawFallbackNotice(Configuration config)
    {
        Toggle(config, "Fall back to free translation",
            config.FallbackToFree, static (c, v) => c.FallbackToFree = v,
            "If the chosen backend fails, is out of quota or is rate limiting,\ntranslate through the free Google/Bing/Yandex endpoints instead of dropping the line.");

        // A cooldown is normal operation, not a fault, so it reads dim rather
        // than as the error the paused notice below shows.
        if (plugin.Translation.CoolingDown)
        {
            DimWrapped(
                $"Rate limited — leaving the provider alone for {plugin.Translation.CooldownRemaining.TotalSeconds:0}s."
                + (config.FallbackToFree ? " Free translation is covering until then." : string.Empty));
        }
        else if (plugin.Translation.UsingFallback)
        {
            DimWrapped("Currently answering through the free fallback, not the provider above.");
        }
    }

    private void DrawPausedNotice()
    {
        if (!plugin.Translation.Paused)
            return;

        // The theme has no error accent; borrow the chat log's error red.
        using (ImRaii.PushColor(ImGuiCol.Text, ChatColors.Default(XivChatType.ErrorMessage)))
        {
            ImGui.TextWrapped(plugin.Translation.LastError ?? "Translation is paused after repeated failures.");
        }

        if (ImGui.Button("Resume##tr-resume"))
            plugin.Translation.Resume();
    }

    private static void DrawLanguageSection(Configuration config)
    {
        SectionHeader("Languages");

        var target = LanguageIndex(config.TargetLanguage, DefaultTargetIndex);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Translate into##tr-target", ref target, LanguageLabels, LanguageLabels.Length))
        {
            config.TargetLanguage = Languages.All[target].Code;
            config.Save();
        }

        using (ImRaii.Disabled(!config.TranslateOutgoing))
        {
            var outgoing = LanguageIndex(config.OutgoingLanguage, DefaultOutgoingIndex);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Send as##tr-outgoing", ref outgoing, LanguageLabels, LanguageLabels.Length))
            {
                config.OutgoingLanguage = Languages.All[outgoing].Code;
                config.Save();
            }
        }
    }

    private static void DrawAppearanceSection(Configuration config)
    {
        SectionHeader("Appearance");

        var color = TranslationService.TranslationColor(config);
        if (ImGui.ColorEdit4("##tr-color", ref color,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
        {
            config.TranslationColor = Services.PackedColor.Pack(color);
            config.Save();
        }

        // The swatch alone doesn't show how the color reads over the panel.
        // Re-read rather than reuse: what gets rendered is the packed value.
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, TranslationService.TranslationColor(config)))
        {
            ImGui.TextUnformatted("Hello there");
        }

        if (config.TranslationColor != 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("reset##tr-color-reset"))
            {
                config.TranslationColor = 0;
                config.Save();
            }
        }

        Toggle(config, "Show original on hover",
            config.ShowTranslationTooltip, static (c, v) => c.ShowTranslationTooltip = v,
            "Hovering a translated line shows the untranslated text.");
    }

    private static void DrawFilterSection(Configuration config)
    {
        SectionHeader("Filters");

        Toggle(config, "Skip my own messages",
            config.SkipOwnMessages, static (c, v) => c.SkipOwnMessages = v,
            "The game echoes what you send back into the log, so without this\nyour own lines get translated a second time.");

        var maxChars = config.MaxTranslateChars;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("Max characters per message##tr-max", ref maxChars, 50, 1000))
        {
            config.MaxTranslateChars = maxChars;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Longer messages are left untranslated, capping what a single message can cost.");

        ImGui.Spacing();
        DimWrapped("Only lines a player typed are ever translated; system, error and NPC text is game-written and already in your language. Untick any player channel you'd rather leave alone.");

        if (ImGui.CollapsingHeader("Channels##tr-channels"))
            DrawTranslateChannelGrid(config);
    }

    /// <summary>
    /// Which player channels to translate. Shares DrawChannelGrid's layout but
    /// not its storage (that one is bound to a TabConfig), and lists only the
    /// kinds that can carry player text — the rest would be dead checkboxes.
    /// </summary>
    private static void DrawTranslateChannelGrid(Configuration config)
    {
        foreach (var (group, channels) in ChannelGroups)
        {
            var eligible = Array.FindAll(channels, c => Services.ChatTypes.IsPlayerChat(c.Type));
            if (eligible.Length == 0)
                continue;

            using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
            {
                ImGui.TextUnformatted(group);
            }

            using var table = ImRaii.Table("##tr-channels" + group, 3);
            if (!table.Success)
                continue;

            foreach (var (type, label) in eligible)
            {
                ImGui.TableNextColumn();
                var enabled = config.TranslateChannels.Contains(type);
                using var tint = ImRaii.PushColor(ImGuiCol.Text, ChatColors.For(type), enabled);
                if (ImGui.Checkbox($"{label}##tr-ch{(ushort)type}", ref enabled))
                {
                    if (enabled)
                        config.TranslateChannels.Add(type);
                    else
                        config.TranslateChannels.Remove(type);
                    config.Save();
                }
            }
        }
    }

    private void DrawUsageSection()
    {
        SectionHeader("Usage");

        using (ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim))
        {
            ImGui.TextUnformatted($"{plugin.Translation.CharsUsed:N0} characters translated");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Counted locally since the last reset.\nDeepL's free tier allows 500,000 characters per month.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset counter##tr-usage-reset"))
            plugin.Translation.ResetCharCount();
    }

    /// <summary>A saved code may not be in this build's list; fall back to the
    /// default rather than silently showing (and later saving) the first entry.</summary>
    private static int LanguageIndex(string code, int fallback)
    {
        var index = Array.FindIndex(Languages.All, l => l.Code == code);
        return index >= 0 ? index : fallback;
    }

    private static int LanguageIndexOrZero(string code) =>
        Math.Max(Array.FindIndex(Languages.All, l => l.Code == code), 0);

    private static void DimWrapped(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, FFTheme.TextDim);
        ImGui.TextWrapped(text);
    }
}
