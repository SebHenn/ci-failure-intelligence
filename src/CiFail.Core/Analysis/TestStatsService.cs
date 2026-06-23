using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Computes per-test flakiness over any store (R26). Test stats are a thin aggregation over recent
/// rows, so this scans <see cref="IAnalysisStore.GetRecent"/> and delegates to the pure
/// <see cref="TestFlakeComputer"/> — uniform across SQLite/EF/Mongo and a remote <c>--server</c>
/// (whose http store implements <c>GetRecent</c> too).
/// </summary>
public static class TestStatsService
{
    public static TestStatsSnapshot Compute(IAnalysisStore store, TestStatsQuery query)
    {
        var rows = store.GetRecent(Math.Max(1, query.ScanLimit));
        return TestFlakeComputer.Compute(rows, query);
    }
}
