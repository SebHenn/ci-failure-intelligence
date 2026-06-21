using CiFail.Core.Storage;

namespace CiFail.Core.Output;

/// <summary>
/// Public JSON contract for a persisted history record (<see cref="StoredAnalysis"/>),
/// used by the <c>cifail serve</c> history endpoints and the matching
/// <see cref="HttpAnalysisStore"/> client. Round-trips both ways so a remote store can
/// reconstruct records faithfully.
/// </summary>
public static class StoredAnalysisJson
{
    public static StoredAnalysisDto ToDto(StoredAnalysis s) => new()
    {
        Id = s.Id,
        AnalyzedAt = s.AnalyzedAt,
        Source = s.Source,
        Ecosystem = s.Ecosystem,
        RuleId = s.RuleId,
        Matched = s.Matched,
        Fingerprint = s.Fingerprint,
        LogHash = s.LogHash,
        Excerpt = s.Excerpt,
        Resolution = s.Resolution,
        ResolvedAt = s.ResolvedAt,
        RepoId = s.RepoId,
        GitCommit = s.GitCommit,
        GitBranch = s.GitBranch,
        GitDirty = s.GitDirty,
        Status = s.Status,
        ResolutionSource = s.ResolutionSource,
        ResolvedCommit = s.ResolvedCommit,
    };

    public static StoredAnalysis FromDto(StoredAnalysisDto d) => new()
    {
        Id = d.Id,
        AnalyzedAt = d.AnalyzedAt,
        Source = d.Source,
        Ecosystem = d.Ecosystem,
        RuleId = d.RuleId,
        Matched = d.Matched,
        Fingerprint = d.Fingerprint,
        LogHash = d.LogHash,
        Excerpt = d.Excerpt,
        Resolution = d.Resolution,
        ResolvedAt = d.ResolvedAt,
        RepoId = d.RepoId,
        GitCommit = d.GitCommit,
        GitBranch = d.GitBranch,
        GitDirty = d.GitDirty,
        Status = d.Status,
        ResolutionSource = d.ResolutionSource,
        ResolvedCommit = d.ResolvedCommit,
    };

    public sealed class StoredAnalysisDto
    {
        public long Id { get; init; }
        public DateTimeOffset AnalyzedAt { get; init; }
        public string Source { get; init; } = "";
        public string Ecosystem { get; init; } = "";
        public string RuleId { get; init; } = "";
        public bool Matched { get; init; }
        public string Fingerprint { get; init; } = "";
        public string LogHash { get; init; } = "";
        public string Excerpt { get; init; } = "";
        public string? Resolution { get; init; }
        public DateTimeOffset? ResolvedAt { get; init; }
        public string? RepoId { get; init; }
        public string? GitCommit { get; init; }
        public string? GitBranch { get; init; }
        public bool GitDirty { get; init; }
        public string Status { get; init; } = AnalysisStatus.Open;
        public string? ResolutionSource { get; init; }
        public string? ResolvedCommit { get; init; }
    }
}
