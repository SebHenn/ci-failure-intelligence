namespace CiFail.Core.Storage;

/// <summary>A persisted analysis row, as read back from history.</summary>
public sealed record StoredAnalysis
{
    public required long Id { get; init; }
    public required DateTimeOffset AnalyzedAt { get; init; }
    public required string Source { get; init; }
    public required string Ecosystem { get; init; }
    public required string RuleId { get; init; }
    public required bool Matched { get; init; }
    public required string Fingerprint { get; init; }
    public required string LogHash { get; init; }
    public required string Excerpt { get; init; }
    public string? Resolution { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>The data needed to persist a new analysis (computed by the service).</summary>
public sealed record AnalysisRecord
{
    public required DateTimeOffset AnalyzedAt { get; init; }
    public required string Source { get; init; }
    public required string Ecosystem { get; init; }
    public required string RuleId { get; init; }
    public required bool Matched { get; init; }
    public required string Fingerprint { get; init; }
    public required string LogHash { get; init; }
    public required string Excerpt { get; init; }
    public required IReadOnlyDictionary<string, int> Terms { get; init; }
}

/// <summary>A history entry plus its term vector, for similarity ranking.</summary>
public sealed record CorpusEntry
{
    public required StoredAnalysis Meta { get; init; }
    public required IReadOnlyDictionary<string, int> Terms { get; init; }
}
