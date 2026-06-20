namespace CiFail.Core.Models;

/// <summary>
/// The outcome of analyzing one log: detected ecosystem, the ranked rule matches,
/// the chosen root cause, a fingerprint, and (in later milestones) similar past
/// failures and an optional AI suggestion.
/// </summary>
public sealed class Analysis
{
    public required string Source { get; init; }

    public required Ecosystem Ecosystem { get; init; }

    /// <summary>All rule matches, highest score first.</summary>
    public required IReadOnlyList<RuleMatch> Matches { get; init; }

    /// <summary>The highest-scoring match, or null when nothing matched.</summary>
    public RuleMatch? RootCause => Matches.Count > 0 ? Matches[0] : null;

    public required FailureFingerprint Fingerprint { get; init; }

    /// <summary>Similar prior failures (populated in M2); empty until then.</summary>
    public IReadOnlyList<SimilarFailure> SimilarFailures { get; init; } =
        Array.Empty<SimilarFailure>();

    /// <summary>Optional AI suggestion (populated in M4 when --ai is used).</summary>
    public AiSuggestion? AiSuggestion { get; init; }

    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when at least one rule matched.</summary>
    public bool HasMatch => Matches.Count > 0;
}

/// <summary>A prior analysis that resembles the current one (M2).</summary>
public sealed class SimilarFailure
{
    public required long Id { get; init; }
    public required double Similarity { get; init; }
    public required string RuleId { get; init; }
    public required DateTimeOffset AnalyzedAt { get; init; }
    public string? Excerpt { get; init; }
    public string? Resolution { get; init; }
}

/// <summary>An optional, AI-generated root-cause/fix suggestion (M4).</summary>
public sealed class AiSuggestion
{
    public required string Model { get; init; }
    public required string RootCause { get; init; }
    public required string Fix { get; init; }
}
