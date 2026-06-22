using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Computes history stats over any store: uses the store's own <see cref="IAnalysisStats"/>
/// when available (efficient, server-side), otherwise falls back to scanning recent rows
/// in-app — mirroring how similarity/fingerprint-count capabilities degrade.
/// </summary>
public static class StatsService
{
    public static StatsSnapshot Compute(IAnalysisStore store, StatsQuery query)
    {
        if (store is IAnalysisStats capable)
            return capable.ComputeStats(query);

        var rows = store.GetRecent(Math.Max(1, query.ScanLimit));
        return StatsComputer.Compute(rows, query);
    }
}
