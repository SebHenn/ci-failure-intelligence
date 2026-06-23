using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Clusters failures over any store: uses the store's own <see cref="IClusterer"/> when available
/// (e.g. a remote server), otherwise loads the corpus and clusters in-app — mirroring how
/// <see cref="StatsService"/> degrades for stats.
/// </summary>
public static class ClusterService
{
    public static ClusterResult Compute(IAnalysisStore store, ClusterQuery query)
    {
        if (store is IClusterer capable)
            return capable.ComputeClusters(query);

        var corpus = store.LoadCorpus(Math.Max(1, query.ScanLimit));
        return FailureClusterer.Compute(corpus, query);
    }
}
