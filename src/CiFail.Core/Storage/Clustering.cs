namespace CiFail.Core.Storage;

/// <summary>Filters/limits for a failure-clustering query (R25).</summary>
public sealed record ClusterQuery
{
    /// <summary>Cosine-similarity threshold (0..1) at which two failures join the same cluster.</summary>
    public double Threshold { get; init; } = 0.5;

    /// <summary>How many clusters to return (largest first); 0 = all.</summary>
    public int Top { get; init; } = 10;

    /// <summary>Only consider analyses at or after this instant (null = all time).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only consider analyses for this repository (null = all repos).</summary>
    public string? RepoId { get; init; }

    /// <summary>Include singleton clusters (a failure that grouped with nothing else).</summary>
    public bool IncludeSingletons { get; init; }

    /// <summary>Max history rows to load and cluster.</summary>
    public int ScanLimit { get; init; } = 2000;
}

/// <summary>One group of near-duplicate failures.</summary>
public sealed record FailureCluster
{
    /// <summary>The dominant rule id in the cluster (its "name"); "unmatched" when none matched.</summary>
    public required string Label { get; init; }

    /// <summary>How many history rows fell into this cluster.</summary>
    public required int Count { get; init; }

    /// <summary>The history ids of the members, newest first.</summary>
    public required IReadOnlyList<long> MemberIds { get; init; }

    /// <summary>The distinct ecosystems represented in the cluster.</summary>
    public required IReadOnlyList<string> Ecosystems { get; init; }

    /// <summary>The most recent occurrence in the cluster.</summary>
    public required DateTimeOffset LastSeen { get; init; }
}

/// <summary>The outcome of a clustering run.</summary>
public sealed record ClusterResult
{
    /// <summary>The clusters, largest first.</summary>
    public required IReadOnlyList<FailureCluster> Clusters { get; init; }

    /// <summary>How many history rows were considered (after the query filters).</summary>
    public required int TotalAnalyzed { get; init; }
}

/// <summary>
/// Optional capability (like <see cref="IAnalysisStats"/> / <see cref="IFingerprintCounter"/>)
/// for clustering failures in the store. Stores that don't implement it get an in-app fallback
/// that loads the corpus and clusters locally (see <c>ClusterService</c>).
/// </summary>
public interface IClusterer
{
    ClusterResult ComputeClusters(ClusterQuery query);
}
