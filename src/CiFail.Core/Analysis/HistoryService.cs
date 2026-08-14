using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Browsing history with filters and paging, routed to the store's own query when it has one
/// (<see cref="IHistoryQuery"/>) and otherwise served by scanning recent rows.
///
/// <para>
/// Mirrors <see cref="StatsService"/> / <see cref="ClusterService"/>: one entry point, one
/// definition of the filters (<see cref="HistoryQuery.Filter"/>), so every backend — SQLite, the
/// EF engines, Mongo, and a remote <c>serve</c> — answers the same question the same way.
/// </para>
/// </summary>
public static class HistoryService
{
    public static HistoryPage Query(IAnalysisStore store, HistoryQuery query)
    {
        if (store is IHistoryQuery queryable)
            return queryable.Query(query);

        // Fallback: pull a bounded window and filter it here. Report the truncation rather than
        // letting a partial answer look complete.
        var scanned = store.GetRecent(query.ScanLimit);
        var matched = query.Filter(scanned).ToList();

        var page = matched
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Max(1, query.Limit))
            .ToList();

        return new HistoryPage(page, matched.Count, Truncated: scanned.Count >= query.ScanLimit);
    }
}
