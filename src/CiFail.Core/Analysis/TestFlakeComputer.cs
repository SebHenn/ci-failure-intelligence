using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Pure aggregation of per-test flakiness over stored analyses (R26) — the single source of truth
/// (mirrors <see cref="StatsComputer"/>). R17 persists each failing test as its own row with
/// <c>source = "&lt;path&gt;::&lt;FullName&gt;"</c>, so this groups those rows by test full name.
///
/// Honest limitation: cifail records <em>failures</em>, not passes — so this surfaces recurring and
/// flaky tests, not a true fails/total pass-rate.
/// </summary>
public static class TestFlakeComputer
{
    public static TestStatsSnapshot Compute(IEnumerable<StoredAnalysis> rows, TestStatsQuery query)
    {
        var tests = rows
            .Where(r => query.Since is null || r.AnalyzedAt >= query.Since)
            .Where(r => query.RepoId is null || string.Equals(r.RepoId, query.RepoId, StringComparison.Ordinal))
            .Select(r => (Row: r, FullName: TestName(r.Source)))
            .Where(x => x.FullName is not null)
            .ToList();

        var groups = tests
            .GroupBy(x => x.FullName!, StringComparer.Ordinal)
            .Select(g => ToStat(g.Key, g.Select(x => x.Row).ToList()))
            .ToList();

        var ranked = groups
            .OrderByDescending(t => t.FailureCount)
            .ThenByDescending(t => t.Flaky)
            .ThenByDescending(t => t.LastSeen)
            .Take(Math.Max(1, query.Top))
            .ToList();

        return new TestStatsSnapshot
        {
            TotalTestFailures = tests.Count,
            DistinctTests = groups.Count,
            Tests = ranked,
            FlakyCount = groups.Count(t => t.Flaky),
        };
    }

    private static TestStat ToStat(string fullName, IReadOnlyList<StoredAnalysis> rows)
    {
        // Flaky: a failure recorded after this test had been resolved (same logic as StatsComputer).
        var firstResolvedAt = rows
            .Where(r => r.ResolvedAt is not null)
            .Select(r => r.ResolvedAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();

        return new TestStat
        {
            FullName = fullName,
            FailureCount = rows.Count,
            DistinctDays = rows.Select(r => r.AnalyzedAt.UtcDateTime.Date).Distinct().Count(),
            OpenCount = rows.Count(r => r.Status == AnalysisStatus.Open),
            LastSeen = rows.Max(r => r.AnalyzedAt),
            Flaky = rows.Any(r => r.AnalyzedAt > firstResolvedAt),
        };
    }

    /// <summary>
    /// Extract the test full name from a report-expanded source (<c>file::FullName</c>); null when the
    /// source isn't a test row. Splits on the first <c>::</c> (the file path won't contain one, while a
    /// C++ test name might), matching how the SARIF artifact uri is derived.
    /// </summary>
    private static string? TestName(string source)
    {
        var sep = source.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return null;
        var name = source[(sep + 2)..].Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
