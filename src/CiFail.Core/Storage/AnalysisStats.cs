namespace CiFail.Core.Storage;

/// <summary>Filters/limits for a stats query.</summary>
public sealed record StatsQuery
{
    /// <summary>Only consider analyses at or after this instant (null = all time).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only consider analyses for this repository (null = all repos).</summary>
    public string? RepoId { get; init; }

    /// <summary>How many entries to return in the "top failures" list.</summary>
    public int Top { get; init; } = 10;

    /// <summary>Max rows a fallback may scan when a store can't aggregate in the DB.</summary>
    public int ScanLimit { get; init; } = 5000;
}

/// <summary>Aggregated counts and signals over stored history.</summary>
public sealed record StatsSnapshot
{
    public required int Total { get; init; }
    public required int Open { get; init; }
    public required int Resolved { get; init; }

    /// <summary>Analyses where no rule matched (a coverage gap signal).</summary>
    public required int Unmatched { get; init; }

    /// <summary>Count of analyses per ecosystem, descending.</summary>
    public required IReadOnlyList<CountByKey> ByEcosystem { get; init; }

    /// <summary>The most frequently recurring fingerprints, descending by count.</summary>
    public required IReadOnlyList<FingerprintStat> TopFailures { get; init; }

    /// <summary>Fraction (0..1) of distinct fingerprints seen more than once.</summary>
    public required double RecurrenceRate { get; init; }

    /// <summary>Mean time from first analysis to resolution, over resolved rows (null if none).</summary>
    public TimeSpan? MeanTimeToResolution { get; init; }

    /// <summary>How many resolved rows contributed to <see cref="MeanTimeToResolution"/>.</summary>
    public int ResolvedWithTiming { get; init; }

    /// <summary>Fingerprints that were resolved and then recurred (the fix didn't stick).</summary>
    public required IReadOnlyList<FingerprintStat> Flaky { get; init; }
}

/// <summary>A (key, count) pair, e.g. an ecosystem and how many analyses it has.</summary>
public sealed record CountByKey(string Key, int Count);

/// <summary>Per-fingerprint aggregation.</summary>
public sealed record FingerprintStat
{
    public required string Fingerprint { get; init; }
    public required string RuleId { get; init; }
    public required int Count { get; init; }
    public required int OpenCount { get; init; }
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>True when this fingerprint recurred after a resolution.</summary>
    public required bool Flaky { get; init; }
}

/// <summary>
/// Optional capability (like <see cref="IFingerprintCounter"/> /
/// <see cref="Similarity.ISimilaritySearch"/>) for computing aggregate stats in the store.
/// Stores that don't implement it get an in-app fallback computed from recent rows
/// (see <c>StatsService</c>).
/// </summary>
public interface IAnalysisStats
{
    StatsSnapshot ComputeStats(StatsQuery query);
}

/// <summary>Filters/limits for a per-test flakiness query (R26).</summary>
public sealed record TestStatsQuery
{
    /// <summary>Only consider analyses at or after this instant (null = all time).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only consider analyses for this repository (null = all repos).</summary>
    public string? RepoId { get; init; }

    /// <summary>How many tests to return (noisiest first).</summary>
    public int Top { get; init; } = 20;

    /// <summary>Max history rows a fallback scans.</summary>
    public int ScanLimit { get; init; } = 5000;
}

/// <summary>Per-test aggregation over history (R26). Counts <em>failures</em>, not runs.</summary>
public sealed record TestStat
{
    /// <summary>The test's full name (e.g. <c>Namespace.Class.Method</c>), parsed from the source.</summary>
    public required string FullName { get; init; }

    /// <summary>How many times this test was recorded failing.</summary>
    public required int FailureCount { get; init; }

    /// <summary>The number of distinct calendar days the test failed on (a spread signal).</summary>
    public required int DistinctDays { get; init; }

    /// <summary>How many of its failures are still open (unresolved).</summary>
    public required int OpenCount { get; init; }

    /// <summary>The most recent failure.</summary>
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>True when this test failed again after one of its failures had been resolved.</summary>
    public required bool Flaky { get; init; }
}

/// <summary>
/// The result of a flakiness query. NB: cifail records failures, not passes, so these are
/// "recurring &amp; flaky tests" — not a true fails/total pass-rate.
/// </summary>
public sealed record TestStatsSnapshot
{
    /// <summary>Total test-failure rows considered (after the query filters).</summary>
    public required int TotalTestFailures { get; init; }

    /// <summary>How many distinct tests appear.</summary>
    public required int DistinctTests { get; init; }

    /// <summary>The noisiest tests, most failures first.</summary>
    public required IReadOnlyList<TestStat> Tests { get; init; }

    /// <summary>How many distinct tests are flaky (resolved, then failed again).</summary>
    public required int FlakyCount { get; init; }
}
