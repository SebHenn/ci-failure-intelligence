namespace CiFail.Core.Models;

/// <summary>
/// A log after ingestion: the original text plus a normalized form with volatile
/// noise (ANSI codes, timestamps, absolute paths) removed for stable matching.
/// </summary>
public sealed class LogDocument
{
    /// <summary>Where the log came from (file path, or "stdin").</summary>
    public required string Source { get; init; }

    /// <summary>The raw log text exactly as read.</summary>
    public required string RawText { get; init; }

    /// <summary>Normalized text used for rule matching and similarity.</summary>
    public required string NormalizedText { get; init; }

    /// <summary>Normalized lines (split from <see cref="NormalizedText"/>).</summary>
    public required IReadOnlyList<string> NormalizedLines { get; init; }
}
