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
