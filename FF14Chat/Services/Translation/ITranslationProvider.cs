using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FF14Chat.Services.Translation;

/// <summary>
/// One translated line. A non-null <paramref name="Error"/> marks this item as
/// failed while the rest of the batch may still have succeeded.
/// </summary>
public readonly record struct TranslationResult(string Text, string? DetectedSource, string? Error);

/// <summary>
/// A translation backend. Implementations are immutable snapshots of the
/// settings they were built from, so changing settings means dropping the
/// provider rather than mutating it.
/// </summary>
public interface ITranslationProvider : IDisposable
{
    /// <summary>Most texts a single request may carry.</summary>
    int MaxBatch { get; }

    /// <summary>
    /// Translates <paramref name="texts"/>. The result list has the same length
    /// and order as the input; whole-request failures throw
    /// <see cref="TranslationException"/> instead.
    /// </summary>
    Task<IReadOnlyList<TranslationResult>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLanguage, CancellationToken ct);
}

/// <summary>
/// A whole-request failure. The flags drive the service's retry policy:
/// <see cref="Retryable"/> covers 5xx and network faults, <see cref="AuthFailure"/>
/// must never be retried (a rejected key fails identically forever), and
/// <see cref="RateLimited"/> must not be retried either — repeating a request
/// the service just refused for volume is what turns a throttle into a ban.
/// </summary>
public sealed class TranslationException(
    string message, bool retryable, bool authFailure = false,
    bool rateLimited = false, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public bool Retryable { get; } = retryable;

    public bool AuthFailure { get; } = authFailure;

    public bool RateLimited { get; } = rateLimited;

    /// <summary>How long the service asked us to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Shared transport for the providers. Never logs or echoes the key.</summary>
internal static class ProviderHttp
{
    public static async Task<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)> SendAsync(
        HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Honour an explicit wait over any guess of ours, in both the
            // delta-seconds and HTTP-date forms the header allows.
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date
                    ? date - DateTimeOffset.UtcNow
                    : null);

            return (response.StatusCode, body, retryAfter > TimeSpan.Zero ? retryAfter : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as a cancellation, which is
            // transient and worth a retry — unlike a real caller cancellation.
            throw new TranslationException("Request timed out", retryable: true);
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException(e.Message, retryable: true);
        }
    }
}
