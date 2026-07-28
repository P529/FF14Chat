namespace FF14Chat.Model;

/// <summary>
/// State of a message's translation; null on <see cref="Message"/> means a
/// translation was never requested for it.
/// </summary>
public sealed record TranslationState
{
    public required TranslationStatus Status { get; init; }

    /// <summary>
    /// The translated body once <see cref="Status"/> is Done. Null when the
    /// line was already in the target language — the renderer then leaves the
    /// original alone rather than echoing it twice.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Language the provider detected (e.g. "JA"), null when unknown.</summary>
    public string? DetectedSource { get; init; }

    /// <summary>Language translated into (e.g. "EN-US"), for the tooltip label.</summary>
    public string? TargetLanguage { get; init; }
}

public enum TranslationStatus
{
    Pending,
    Done,
    Failed,
}
