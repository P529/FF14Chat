using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GTranslate.Translators;

namespace FF14Chat.Services.Translation;

/// <summary>
/// Google, Bing and Yandex through their free public web endpoints, via
/// GTranslate. No account, and the library carries its own fallback chain: it
/// walks the services in order until one answers, so an endpoint changing
/// shape degrades to the next rather than to nothing.
///
/// Quality sits below DeepL and well below an LLM, and the endpoints are
/// unofficial — this is the "works with no setup" tier, not the good one.
/// </summary>
internal sealed class MachineTranslateProvider : ITranslationProvider
{
    private readonly AggregateTranslator translator = new();

    // One HTTP round trip per line, run in sequence below: these endpoints
    // rate-limit by IP, and a burst of parallel requests is what trips them.
    public int MaxBatch => 8;

    public async Task<IReadOnlyList<TranslationResult>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
    {
        // GTranslate speaks ISO 639-1; the rest of the plugin speaks DeepL's
        // codes, where the region half ("EN-US") has no equivalent here.
        var target = Languages.BaseCode(targetLanguage).ToLowerInvariant();

        var results = new TranslationResult[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await translator.TranslateAsync(texts[i], target).ConfigureAwait(false);
                results[i] = new TranslationResult(
                    result.Translation,
                    result.SourceLanguage?.ISO6391?.ToUpperInvariant(),
                    null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One bad line shouldn't sink the batch: the services reject
                // individual inputs often enough (length, unsupported script).
                results[i] = new TranslationResult(string.Empty, null, e.Message);
            }
        }

        return results;
    }

    public void Dispose() => translator.Dispose();
}
