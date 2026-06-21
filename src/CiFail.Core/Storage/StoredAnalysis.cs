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

    // Git correlation (R3). Null when the analysis happened outside a git repo.
    public string? RepoId { get; init; }
    public string? GitCommit { get; init; }
    public string? GitBranch { get; init; }
    public bool GitDirty { get; init; }

    /// <summary>"open" while unresolved, "resolved" once fixed.</summary>
    public string Status { get; init; } = AnalysisStatus.Open;

    /// <summary>"manual" (via <c>cifail resolve</c>) or "auto" (git-correlated); null while open.</summary>
    public string? ResolutionSource { get; init; }

    /// <summary>The commit credited with the fix, for auto-resolutions.</summary>
    public string? ResolvedCommit { get; init; }
}

/// <summary>The lifecycle states a failure record can be in.</summary>
public static class AnalysisStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
}

/// <summary>How a resolution was recorded.</summary>
public static class ResolutionSource
{
    public const string Manual = "manual";
    public const string Auto = "auto";
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

    // Git correlation (R3). Null when analyzing outside a git repo.
    public string? RepoId { get; init; }
    public string? GitCommit { get; init; }
    public string? GitBranch { get; init; }
    public bool GitDirty { get; init; }
}

/// <summary>A history entry plus its term vector, for similarity ranking.</summary>
public sealed record CorpusEntry
{
    public required StoredAnalysis Meta { get; init; }
    public required IReadOnlyDictionary<string, int> Terms { get; init; }
}
