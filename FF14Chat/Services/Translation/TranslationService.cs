using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using FF14Chat.Model;

namespace FF14Chat.Services.Translation;

/// <summary>
/// Owns the translation queue, the result cache and the active provider.
/// Threading: requests are made on the framework thread and resolved on a
/// single background worker, so results cross threads through the volatile
/// <see cref="Message.Translation"/> field and nothing else is shared.
/// </summary>
public sealed partial class TranslationService : IDisposable
{
    private const int CacheCapacity = 2000;
    private const int DebounceMs = 250;
    private const int MaxInFlight = 2;
    private const int MaxRetries = 3;
    private const int FailuresBeforePause = 5;

    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);

    // Rate-limit cooldown when the service names no Retry-After: a minute,
    // doubling per repeat strike, capped so it always recovers on its own.
    private static readonly TimeSpan MinCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);
    private const int MaxCooldownStreak = 4;

    /// <summary>Soft teal; distinct from every stock channel color.</summary>
    public static readonly Vector4 DefaultTranslationColor = new(0.45f, 0.80f, 0.78f, 1f);

    private readonly Configuration config;
    private readonly HttpClient http;
    private readonly Channel<Message> queue =
        Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim inFlight = new(MaxInFlight, MaxInFlight);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;

    private readonly object cacheGate = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> cacheOrder = [];

    private readonly object providerGate = new();
    private ITranslationProvider? provider;
    private MachineTranslateProvider? fallback;

    private long baseChars;
    private long sessionChars;
    private int consecutiveFailures;
    private int cooldownStreak;
    private DateTime cooldownUntil = DateTime.MinValue;
    private DateTime lastPersist = DateTime.UtcNow;
    private volatile bool paused;
    private volatile string? lastError;

    public TranslationService(Configuration config)
    {
        this.config = config;
        baseChars = config.TranslationCharsUsed;

        var version = typeof(TranslationService).Assembly.GetName().Version?.ToString(3) ?? "0";

        // Decompression is the handler's job: it advertises what it can unpack
        // and unpacks it. A provider adding its own Accept-Encoding would get
        // compressed bytes back with nothing to decode them.
        http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        // Providers that need to present as something else set their own.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FF14Chat/{version}");

        worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Raised once per resolved batch, on the worker thread. Consumers must
    /// marshal to the main thread before touching anything the renderer owns.
    /// </summary>
    public event Action? Changed;

    /// <summary>Characters billed this session plus the persisted running total.</summary>
    public long CharsUsed => Interlocked.Read(ref baseChars) + Interlocked.Read(ref sessionChars);

    /// <summary>Last error surfaced by a provider, for the settings tab. Null when healthy.</summary>
    public string? LastError => lastError;

    /// <summary>True while the service has auto-paused after repeated hard failures.</summary>
    public bool Paused => paused;

    /// <summary>
    /// Queue an incoming message for translation. Cheap, non-blocking, safe to
    /// call from the framework thread. No-op when disabled, filtered out, or
    /// already resolved from cache.
    /// </summary>
    public void RequestIncoming(Message message)
    {
        if (!config.TranslateIncoming || paused || message.Translation != null || !HasKey())
            return;
        if (ShouldSkip(message))
            return;

        var target = config.TargetLanguage;
        var cached = Cached(CacheKey(target, message.Text));
        if (cached != null)
        {
            // The message was routed a moment ago, so its tab revision already
            // moved this frame — no Changed signal needed for a cache hit.
            message.Translation = cached;
            return;
        }

        message.Translation = new TranslationState
        {
            Status = TranslationStatus.Pending,
            TargetLanguage = target,
        };
        queue.Writer.TryWrite(message);
    }

    /// <summary>
    /// Translate one message because the user asked for it, rather than because
    /// it matched the filters. Everything the filters exist for — the incoming
    /// toggle, the channel set, own-message and length skips, an existing
    /// result — is deliberately ignored: they answer "should this happen
    /// unprompted", and this was prompted. A missing provider or an active
    /// pause still stops it; the menu offering this greys out for both.
    /// </summary>
    public void RequestManual(Message message)
    {
        if (Provider() == null)
        {
            lastError = "No API key set for the selected provider.";
            return;
        }

        var target = config.TargetLanguage;
        if (Cached(CacheKey(target, message.Text)) is { } cached)
        {
            message.Translation = cached;
            return;
        }

        message.Translation = new TranslationState
        {
            Status = TranslationStatus.Pending,
            TargetLanguage = target,
        };
        queue.Writer.TryWrite(message);
    }

    /// <summary>Drops a translation so the line renders as it arrived.</summary>
    public static void ShowOriginal(Message message) => message.Translation = null;

    /// <summary>
    /// Translate outgoing input into the configured outgoing language. Bypasses
    /// the incoming queue and the channel filter. Returns null on failure, so
    /// the caller can keep the user's draft.
    /// </summary>
    public async Task<string?> TranslateOutgoingAsync(string text, CancellationToken ct)
    {
        // MaxTranslateChars deliberately does not apply: it caps what incoming
        // spam can cost, and applying it here would refuse to send a line the
        // game itself would have accepted. The chat box's own limit bounds this.
        var active = Provider();
        if (active == null)
        {
            lastError = "No API key set for the selected provider.";
            return null;
        }

        if (text.Length == 0 || text.Length > ChatSender.MaxBytes)
            return null;

        var target = config.OutgoingLanguage;
        var key = CacheKey(target, text);
        if (Cached(key) is { Status: TranslationStatus.Done } hit)
            return hit.Text ?? text;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
            var results = await SendAsync(active, [text], target, linked.Token).ConfigureAwait(false);
            if (results is not [{ Error: null } result])
                return null;

            Store(key, new TranslationState
            {
                Status = TranslationStatus.Done,
                Text = result.Text,
                DetectedSource = result.DetectedSource,
                TargetLanguage = target,
            });
            PersistChars(force: false);
            return result.Text;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Round-trip probe for the settings Test button: a human-readable result
    /// line, or the API's error message. Never throws.
    /// </summary>
    public async Task<string> TestAsync()
    {
        const string Probe = "Hello, world!";

        var active = Provider();
        if (active == null)
            return "No API key set for the selected provider.";

        try
        {
            var target = config.TargetLanguage;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var results = await active.TranslateAsync([Probe], target, timeout.Token).ConfigureAwait(false);
            if (results.Count == 0)
                return "The provider returned no result.";
            if (results[0].Error is { } itemError)
                return itemError;

            // A working round trip clears whatever tripped the auto-pause.
            paused = false;
            Interlocked.Exchange(ref consecutiveFailures, 0);
            lastError = null;
            CountChars([Probe]);

            return $"OK: \"{Probe}\" -> \"{results[0].Text}\" ({Languages.Label(target)})";
        }
        catch (Exception e)
        {
            lastError = e.Message;
            return e.Message;
        }
    }

    public void ResetCharCount()
    {
        Interlocked.Exchange(ref baseChars, 0);
        Interlocked.Exchange(ref sessionChars, 0);
        config.TranslationCharsUsed = 0;
        config.Save();
    }

    /// <summary>Clears the pause and any rate-limit cooldown; user-driven, so
    /// the explicit ask outranks the backoff we picked for them.</summary>
    public void Resume()
    {
        paused = false;
        Interlocked.Exchange(ref consecutiveFailures, 0);
        cooldownUntil = DateTime.MinValue;
        cooldownStreak = 0;
        lastError = null;
    }

    /// <summary>Drops the provider so the next request picks up edited settings.</summary>
    public void InvalidateProvider()
    {
        lock (providerGate)
        {
            provider?.Dispose();
            provider = null;
        }
    }

    /// <summary>Which backend answered last, for the settings tab.</summary>
    public bool UsingFallback { get; private set; }

    /// <summary>
    /// Resolves the color translated lines render in, unpacking the RGBA
    /// override the settings tab stores (0 means "use the built-in default").
    /// </summary>
    public static Vector4 TranslationColor(Configuration config)
    {
        var packed = config.TranslationColor;
        return packed == 0
            ? DefaultTranslationColor
            : new Vector4(
                (packed & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                ((packed >> 16) & 0xFF) / 255f,
                1f);
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        shutdown.Cancel();

        try
        {
            worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation on unload; nothing worth reporting.
        }

        PersistChars(force: true);
        InvalidateProvider();

        // Not covered by InvalidateProvider: the fallback deliberately outlives
        // provider switches, and it owns its own transport.
        lock (providerGate)
        {
            fallback?.Dispose();
            fallback = null;
        }

        http.Dispose();
        inFlight.Dispose();
        shutdown.Dispose();
    }

    // Anything that survives this is untranslatable noise: URLs, :shortcode:
    // emotes, punctuation, symbols, separators and digits are all stripped, so
    // an empty remainder means the line carries no words.
    [GeneratedRegex(@"(?:https?://|www\.)\S+|:[\w+-]{2,}:|[\p{P}\p{S}\p{Z}\p{C}\p{N}]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    private bool HasKey() => (TranslationProviderKind)config.TranslationProvider switch
    {
        TranslationProviderKind.MachineTranslate => true,
        TranslationProviderKind.DeepL => config.DeepLApiKey.Length > 0,
        _ => config.LlmApiKey.Length > 0,
    };

    private bool ShouldSkip(Message message)
    {
        var text = message.Text;
        if (text.Length > config.MaxTranslateChars || text.AsSpan().Trim().Length < 2)
            return true;
        // Only lines a player typed, and only from the channels still ticked.
        var kind = ChatTypes.Mask(message.Type);
        if (!ChatTypes.IsPlayerChat(kind) || message.Sender.Length == 0)
            return true;
        if (!config.TranslateChannels.Contains(kind))
            return true;
        if (config.SkipOwnMessages && IsOwnMessage(message))
            return true;

        return NoiseRegex().Replace(text, string.Empty).Length == 0;
    }

    /// <summary>
    /// Whether the local player wrote this line. Mirrors ChatCapture's sender
    /// resolution: own messages carry no player payload, so the plain-text name
    /// is the only signal. Outgoing tells are own messages by definition even
    /// though their sender field names the recipient.
    /// </summary>
    private static bool IsOwnMessage(Message message)
    {
        if (ChatTypes.Mask(message.Type) == XivChatType.TellOutgoing)
            return true;

        if (!Plugin.PlayerState.IsLoaded
            || Plugin.PlayerState.CharacterName is not { Length: > 0 } localName)
        {
            return false;
        }

        return message.SenderPlayer == null
            ? message.Sender.EndsWith(localName, StringComparison.Ordinal)
            : message.SenderPlayer.StartsWith(localName + "@", StringComparison.Ordinal);
    }

    private ITranslationProvider? Provider()
    {
        lock (providerGate)
        {
            if (provider != null)
                return provider;

            // Unknown ids (a removed backend in an older config, or one a newer
            // build wrote) land on the keyless default rather than nothing.
            var kind = (TranslationProviderKind)config.TranslationProvider;
            provider = kind switch
            {
                TranslationProviderKind.DeepL when config.DeepLApiKey.Length > 0
                    => new DeepLProvider(http, config.DeepLApiKey),
                TranslationProviderKind.Anthropic or TranslationProviderKind.OpenAiCompatible
                    when config.LlmApiKey.Length > 0
                    => new LlmProvider(http, kind, config.LlmApiKey, config.LlmModel, config.LlmBaseUrl),

                // Keyed backends with no key fall through to null: nothing to
                // send with, and silently using a different service than the
                // one selected would be worse than not translating.
                TranslationProviderKind.DeepL or TranslationProviderKind.Anthropic
                    or TranslationProviderKind.OpenAiCompatible => null,
                _ => new MachineTranslateProvider(),
            };

            return provider;
        }
    }

    private async Task RunAsync()
    {
        var token = shutdown.Token;
        var reader = queue.Reader;
        var batch = new List<Message>();

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                // Let neighbouring lines pile up: a busy channel then costs one
                // request instead of one per message.
                await Task.Delay(DebounceMs, token).ConfigureAwait(false);

                var active = paused ? null : Provider();
                if (active == null)
                {
                    Drain(reader);
                    continue;
                }

                batch.Clear();
                while (batch.Count < active.MaxBatch && reader.TryRead(out var message))
                    batch.Add(message);

                if (batch.Count == 0)
                    continue;

                await inFlight.WaitAsync(token).ConfigureAwait(false);
                var work = batch.ToArray();
                _ = Task.Run(() => TranslateBatchAsync(active, work), CancellationToken.None);

                PersistChars(force: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Unloading.
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Translation worker stopped");
        }
    }

    /// <summary>Fails everything queued, so no line is left spinning forever.</summary>
    private void Drain(ChannelReader<Message> reader)
    {
        var drained = false;
        while (reader.TryRead(out var message))
        {
            message.Translation = Failed(config.TargetLanguage);
            drained = true;
        }

        if (drained)
            Changed?.Invoke();
    }

    private async Task TranslateBatchAsync(ITranslationProvider active, Message[] batch)
    {
        var target = config.TargetLanguage;
        try
        {
            var targetBase = Languages.BaseCode(target);

            // Identical lines (spam, duplicated shouts, repeated party calls)
            // cost one API item and fan the single result back out.
            var texts = new List<string>();
            var groups = new Dictionary<string, List<Message>>(StringComparer.Ordinal);
            foreach (var message in batch)
            {
                if (!groups.TryGetValue(message.Text, out var group))
                {
                    groups[message.Text] = group = [];
                    texts.Add(message.Text);
                }

                group.Add(message);
            }

            var results = await SendAsync(active, texts, target, shutdown.Token).ConfigureAwait(false);
            if (results == null)
            {
                foreach (var message in batch)
                    message.Translation = Failed(target);
                return;
            }

            for (var i = 0; i < texts.Count; i++)
            {
                var result = results[i];
                TranslationState state;
                if (result.Error != null)
                {
                    state = Failed(target);
                }
                else
                {
                    // Nothing to show when the line was already in the target
                    // language; the renderer leaves the original alone.
                    var sameLanguage = result.DetectedSource != null
                        && string.Equals(
                            Languages.BaseCode(result.DetectedSource), targetBase, StringComparison.OrdinalIgnoreCase);
                    state = new TranslationState
                    {
                        Status = TranslationStatus.Done,
                        Text = sameLanguage ? null : result.Text,
                        DetectedSource = result.DetectedSource,
                        TargetLanguage = target,
                    };
                    Store(CacheKey(target, texts[i]), state);
                }

                foreach (var message in groups[texts[i]])
                    message.Translation = state;
            }
        }
        catch (OperationCanceledException)
        {
            // Unloading mid-request.
        }
        catch (Exception e)
        {
            if (!shutdown.IsCancellationRequested)
                Plugin.Log.Error(e, "Translation batch failed");

            foreach (var message in batch)
                message.Translation = Failed(target);
        }
        finally
        {
            inFlight.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The chosen provider, then the free machine translators if it gave up and
    /// the fallback is on. Null when nothing could answer; only then does the
    /// failure count toward pausing, since a served batch is not an outage.
    /// </summary>
    private async Task<IReadOnlyList<TranslationResult>?> SendAsync(
        ITranslationProvider active, IReadOnlyList<string> texts, string target, CancellationToken token)
    {
        // Still inside a cooldown the primary asked for: don't touch it at all,
        // just take the fallback if there is one. Retrying a service that is
        // refusing us for volume is how a throttle becomes a block.
        var (results, authFailure) = CoolingDown
            ? (null, false)
            : await AttemptAsync(active, texts, target, token).ConfigureAwait(false);
        if (results != null)
        {
            UsingFallback = false;
            return results;
        }

        // Whatever stopped the primary — dead endpoint, exhausted quota, a key
        // they no longer accept — is exactly what the free tier is here for.
        // lastError keeps the primary's reason so the settings tab still says
        // why it isn't being used.
        if (config.FallbackToFree && active is not MachineTranslateProvider)
        {
            var (fallbackResults, _) = await AttemptAsync(Fallback(), texts, target, token).ConfigureAwait(false);
            if (fallbackResults != null)
            {
                UsingFallback = true;
                return fallbackResults;
            }
        }

        // A cooldown is already the policy for "come back later" — letting it
        // also count toward the failure pause would stop translation entirely
        // over something that recovers by itself.
        if (CoolingDown)
            return null;

        if (authFailure || Interlocked.Increment(ref consecutiveFailures) >= FailuresBeforePause)
            paused = true;

        return null;
    }

    /// <summary>
    /// One provider, with exponential backoff on transient failures. Null
    /// results mean it is beyond saving; the flag separates "rejected us" from
    /// "broke", which decides whether retrying it later is worth anything.
    /// </summary>
    private async Task<(IReadOnlyList<TranslationResult>? Results, bool AuthFailure)> AttemptAsync(
        ITranslationProvider active, IReadOnlyList<string> texts, string target, CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var results = await active.TranslateAsync(texts, target, token).ConfigureAwait(false);
                lastError = null;
                Interlocked.Exchange(ref consecutiveFailures, 0);
                CountChars(texts);
                return (results, false);
            }
            catch (TranslationException e) when (e.AuthFailure)
            {
                // A rejected key fails identically forever; stop dead rather
                // than hammering the endpoint with a batch every few seconds.
                lastError = e.Message;
                return (null, true);
            }
            catch (TranslationException e) when (e.RateLimited)
            {
                // Never retried inline. The cooldown is what the service asked
                // for, or an escalating guess when it didn't say — each strike
                // while already cooling doubles it, so repeatedly walking into
                // the same limit backs further off instead of settling into a
                // steady drip of refused requests.
                BeginCooldown(e.RetryAfter);
                lastError = $"{e.Message}; paused until {cooldownUntil.ToLocalTime():HH:mm:ss}";
                return (null, false);
            }
            catch (TranslationException e) when (e.Retryable && attempt < MaxRetries)
            {
                lastError = e.Message;
                await Task.Delay(delay, token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, BackoffCap.Ticks));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastError = e.Message;
                return (null, false);
            }
        }
    }

    /// <summary>Whether the primary provider is inside a rate-limit cooldown.</summary>
    public bool CoolingDown => DateTime.UtcNow < cooldownUntil;

    /// <summary>How much longer the primary stays untouched; zero when it doesn't.</summary>
    public TimeSpan CooldownRemaining
    {
        get
        {
            var remaining = cooldownUntil - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private void BeginCooldown(TimeSpan? requested)
    {
        // Doubling only while a cooldown is still running: an isolated limit
        // hours later is not evidence the last one was too short.
        cooldownStreak = CoolingDown ? Math.Min(cooldownStreak + 1, MaxCooldownStreak) : 0;

        var wait = requested ?? TimeSpan.FromTicks(MinCooldown.Ticks << cooldownStreak);
        if (wait > MaxCooldown)
            wait = MaxCooldown;

        cooldownUntil = DateTime.UtcNow + wait;
        Plugin.Log.Information(
            "Translation provider rate limited; leaving it alone for {Seconds:0}s", wait.TotalSeconds);
    }

    /// <summary>
    /// The free machine translators, built on first need. Kept apart from
    /// <see cref="provider"/> so switching provider in the settings doesn't
    /// discard it, and so it survives the primary being invalidated mid-batch.
    /// </summary>
    private MachineTranslateProvider Fallback()
    {
        lock (providerGate)
        {
            return fallback ??= new MachineTranslateProvider();
        }
    }

    private static TranslationState Failed(string target) => new()
    {
        Status = TranslationStatus.Failed,
        TargetLanguage = target,
    };

    private void CountChars(IReadOnlyList<string> texts)
    {
        var chars = 0L;
        foreach (var text in texts)
            chars += text.Length;

        Interlocked.Add(ref sessionChars, chars);
    }

    /// <summary>
    /// Persists the running character count. Throttled because a busy channel
    /// would otherwise rewrite the config file every few seconds; the forced
    /// call on dispose is what makes the total survive a reload.
    /// </summary>
    private void PersistChars(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - lastPersist < PersistInterval)
            return;

        lastPersist = now;
        var total = CharsUsed;
        if (total == config.TranslationCharsUsed)
            return;

        config.TranslationCharsUsed = total;

        // Called from the worker; the settings tab saves the same file from the
        // framework thread. Marshalling makes the two writes take turns (and
        // runs inline when this is the forced call during unload, which is
        // already on that thread).
        Plugin.Framework.RunOnFrameworkThread(config.Save);
    }

    private static string CacheKey(string target, string text) => $"{target}|{text}";

    private readonly record struct CacheEntry(string Key, TranslationState State);

    private TranslationState? Cached(string key)
    {
        lock (cacheGate)
        {
            if (!cache.TryGetValue(key, out var node))
                return null;

            cacheOrder.Remove(node);
            cacheOrder.AddLast(node);
            return node.Value.State;
        }
    }

    private void Store(string key, TranslationState state)
    {
        lock (cacheGate)
        {
            if (cache.TryGetValue(key, out var existing))
                cacheOrder.Remove(existing);

            cache[key] = cacheOrder.AddLast(new CacheEntry(key, state));

            while (cache.Count > CacheCapacity)
            {
                var oldest = cacheOrder.First!;
                cacheOrder.RemoveFirst();
                cache.Remove(oldest.Value.Key);
            }
        }
    }
}
