namespace CiFail.Providers.Ef;

/// <summary>
/// EF Core row for an analysis. Timestamps are stored as ISO-8601 ("O") strings — the
/// same shape the hand-written SQLite repo uses — so every relational provider persists
/// them identically and we sidestep per-provider <c>DateTimeOffset</c> quirks. Term
/// vectors live in <see cref="Tokens"/> as a JSON string, matching the SQLite schema.
/// </summary>
public sealed class AnalysisEntity
{
    public long Id { get; set; }
    public string AnalyzedAt { get; set; } = "";
    public string Source { get; set; } = "";
    public string Ecosystem { get; set; } = "";
    public string RuleId { get; set; } = "";
    public bool Matched { get; set; }
    public string Fingerprint { get; set; } = "";
    public string LogHash { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string Tokens { get; set; } = "{}";
    public string? Resolution { get; set; }
    public string? ResolvedAt { get; set; }
}
