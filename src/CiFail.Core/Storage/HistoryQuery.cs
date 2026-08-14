namespace CiFail.Core.Storage;

/// <summary>
/// Filters and paging for browsing history.
///
/// <para>
/// Every consumer previously fetched the newest N rows and filtered them in memory. On the
/// dashboard that N was 200, so "show me resolved failures" could only ever find one among the
/// most recent 200 records — an older resolved failure was unreachable, and the ecosystem
/// dropdown was built from the same window, so a rarely-seen ecosystem vanished from the filter
/// entirely. That is a wrong answer, not a slow one.
/// </para>
/// </summary>
public sealed class HistoryQuery
{
    /// <summary>Rows per page.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Rows to skip, for paging.</summary>
    public int Offset { get; init; }

    /// <summary>Only analyses at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exact repository id (the repo's root commit).</summary>
    public string? RepoId { get; init; }

    /// <summary>Exact ecosystem name, lowercase (e.g. <c>node</c>).</summary>
    public string? Ecosystem { get; init; }

    /// <summary><see cref="AnalysisStatus.Open"/> or <see cref="AnalysisStatus.Resolved"/>.</summary>
    public string? Status { get; init; }

    /// <summary>Exact rule id.</summary>
    public string? RuleId { get; init; }

    /// <summary>
    /// Case-insensitive substring over source, fingerprint and excerpt — the "I know roughly what
    /// it said" search.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// How many rows the in-memory fallback may pull before filtering, for stores that don't
    /// implement <see cref="IHistoryQuery"/>. Bounded so the fallback can't turn into a table scan.
    /// </summary>
    public int ScanLimit { get; init; } = 5000;

    /// <summary>True when nothing is being filtered, only paged.</summary>
    public bool IsUnfiltered =>
        Since is null && RepoId is null && Ecosystem is null &&
        Status is null && RuleId is null && string.IsNullOrWhiteSpace(Search);

    /// <summary>Apply the filters to an in-memory sequence — the single definition of what each means.</summary>
    public IEnumerable<StoredAnalysis> Filter(IEnumerable<StoredAnalysis> rows)
    {
        if (Since is { } since) rows = rows.Where(r => r.AnalyzedAt >= since);
        if (!string.IsNullOrWhiteSpace(RepoId)) rows = rows.Where(r => r.RepoId == RepoId);
        if (!string.IsNullOrWhiteSpace(Status)) rows = rows.Where(r => r.Status == Status);
        if (!string.IsNullOrWhiteSpace(RuleId)) rows = rows.Where(r => r.RuleId == RuleId);

        if (!string.IsNullOrWhiteSpace(Ecosystem))
            rows = rows.Where(r => string.Equals(r.Ecosystem, Ecosystem, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(Search))
        {
            rows = rows.Where(r =>
                r.Source.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                r.Fingerprint.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                r.Excerpt.Contains(Search, StringComparison.OrdinalIgnoreCase));
        }

        return rows;
    }
}

/// <summary>One page of history, plus how many rows matched in total.</summary>
/// <param name="Items">The rows for this page, newest first.</param>
/// <param name="Total">
/// Total matching rows. For a store answering from the in-memory fallback this counts only
/// within <see cref="HistoryQuery.ScanLimit"/> — see <see cref="Truncated"/>.
/// </param>
/// <param name="Truncated">
/// True when the fallback hit its scan limit, so <paramref name="Total"/> is a floor rather than
/// an exact count. Surfaced rather than hidden: silently reporting a truncated total as exact is
/// how "we searched everything" becomes a lie.
/// </param>
public sealed record HistoryPage(
    IReadOnlyList<StoredAnalysis> Items,
    int Total,
    bool Truncated = false);

/// <summary>
/// Optional capability (like <see cref="IFingerprintCounter"/> and <see cref="IAnalysisStats"/>):
/// run a <see cref="HistoryQuery"/> in the database rather than in memory.
///
/// <para>
/// A side-interface rather than a member of <see cref="IAnalysisStore"/> so existing stores and
/// test fakes keep compiling; <see cref="Analysis.HistoryService"/> falls back for any store that
/// doesn't implement it, and both paths share <see cref="HistoryQuery.Filter"/> so they cannot
/// disagree about what a filter means.
/// </para>
/// </summary>
public interface IHistoryQuery
{
    HistoryPage Query(HistoryQuery query);
}

/// <summary>What to delete, and whether to actually do it.</summary>
/// <param name="Before">Delete analyses recorded strictly before this instant.</param>
/// <param name="ResolvedOnly">
/// Keep anything still open regardless of age. On by default at the CLI: an old failure nobody
/// resolved is usually the one you least want to forget.
/// </param>
/// <param name="DryRun">Count what would go without deleting it.</param>
public sealed record PruneRequest(DateTimeOffset Before, bool ResolvedOnly, bool DryRun);

/// <summary>How many rows a prune removed (or would remove).</summary>
public sealed record PruneResult(int Deleted, bool DryRun);

/// <summary>
/// Optional capability: delete old analyses.
///
/// <para>
/// History had no delete path at all — no method on <see cref="IAnalysisStore"/>, no command —
/// so <c>history.db</c> grew for the life of the install, holding a log excerpt and a term bag
/// per failure. That is both unbounded disk and an ever-growing pile of log text at rest, which
/// SECURITY.md is explicit can contain secrets.
/// </para>
/// </summary>
public interface IPrunableStore
{
    PruneResult Prune(PruneRequest request);
}
