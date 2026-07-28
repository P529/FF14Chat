using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FF14Chat.Services.Translation;

/// <summary>DeepL's v2 translate endpoint.</summary>
internal sealed class DeepLProvider : ITranslationProvider
{
    private const string FreeHost = "https://api-free.deepl.com";
    private const string ProHost = "https://api.deepl.com";

    private readonly HttpClient http;
    private readonly string key;
    private readonly string endpoint;

    public DeepLProvider(HttpClient http, string key)
    {
        this.http = http;
        this.key = key;

        // Free-tier keys are suffixed ":fx" and are only valid on the free host.
        var host = key.EndsWith(":fx", StringComparison.Ordinal) ? FreeHost : ProHost;
        endpoint = host + "/v2/translate";
    }

    public int MaxBatch => 50;

    public async Task<IReadOnlyList<TranslationResult>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
        request.Content = new StringContent(
            BuildBody(texts, targetLanguage), Encoding.UTF8, "application/json");

        var (status, body, retryAfter) = await ProviderHttp.SendAsync(http, request, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            throw Failure(status, retryAfter);

        return Parse(body, texts.Count);
    }

    /// <summary>The shared HttpClient outlives every provider; nothing to release.</summary>
    public void Dispose()
    {
    }

    private static string BuildBody(IReadOnlyList<string> texts, string targetLanguage)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("text");
            foreach (var text in texts)
                writer.WriteStringValue(text);
            writer.WriteEndArray();
            writer.WriteString("target_lang", targetLanguage);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static TranslationResult[] Parse(string body, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() != expected)
            {
                throw new TranslationException("DeepL returned an unexpected response", retryable: false);
            }

            var results = new TranslationResult[expected];
            var index = 0;
            foreach (var item in translations.EnumerateArray())
            {
                results[index++] = new TranslationResult(
                    item.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("detected_source_language", out var source) ? source.GetString() : null,
                    null);
            }

            return results;
        }
        catch (JsonException)
        {
            throw new TranslationException("DeepL returned malformed JSON", retryable: false);
        }
    }

    private static TranslationException Failure(HttpStatusCode status, TimeSpan? retryAfter) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new TranslationException("DeepL rejected the API key", retryable: false, authFailure: true),
        (HttpStatusCode)456 => new TranslationException("DeepL quota exceeded", retryable: false),
        HttpStatusCode.TooManyRequests =>
            new TranslationException(
                "DeepL rate limit reached", retryable: false, rateLimited: true, retryAfter: retryAfter),
        >= HttpStatusCode.InternalServerError =>
            new TranslationException($"DeepL server error ({(int)status})", retryable: true),
        _ => new TranslationException($"DeepL error {(int)status}", retryable: false),
    };
}
