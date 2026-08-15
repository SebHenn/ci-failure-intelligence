using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

public class StatsComputerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static StoredAnalysis Row(
        long id, string fingerprint, DateTimeOffset at,
        string status = AnalysisStatus.Open, DateTimeOffset? resolvedAt = null,
        string ecosystem = "dotnet", bool matched = true) => new()
    {
        Id = id,
        AnalyzedAt = at,
        Source = "s",
        Ecosystem = ecosystem,
        RuleId = fingerprint.Split(':')[0],
        Matched = matched,
        Fingerprint = fingerprint,
        LogHash = "h",
        Excerpt = "x",
        Status = status,
        ResolvedAt = resolvedAt,
    };

    /// <summary>
    /// Aggregation loads whole rows and counts them in memory, so past the scan limit every
    /// figure — the total, the recurrence rate, the mean time to resolution — silently describes
    /// the newest rows only. "1,204 failures" when there are 40,000 is not a rounding error, so
    /// the snapshot has to say when it was capped.
    /// </summary>
    [Fact]
    public void A_capped_scan_reports_itself_as_truncated()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Row(i, $"r:{i}", T0)).ToList();
        var store = new FixedStore(rows);

        var capped = StatsService.Compute(store, new StatsQuery { ScanLimit = 10 });
        var roomy = StatsService.Compute(store, new StatsQuery { ScanLimit = 500 });

        capped.Truncated.Should().BeTrue("the limit, not the data, decided where counting stopped");
        roomy.Truncated.Should().BeFalse();
        roomy.Total.Should().Be(10);
    }

    /// <summary>A store returning exactly what was asked for, to exercise the scan-limit edge.</summary>
    private sealed class FixedStore : IAnalysisStore
    {
        private readonly IReadOnlyList<StoredAnalysis> _rows;
        public FixedStore(IReadOnlyList<StoredAnalysis> rows) => _rows = rows;

        public IReadOnlyList<StoredAnalysis> GetRecent(int limit) => _rows.Take(limit).ToList();

        public long Save(AnalysisRecord record) => 0;
        public StoredAnalysis? GetById(long id) => null;
        public IReadOnlyList<CorpusEntry> LoadCorpus(int max) => Array.Empty<CorpusEntry>();
        public bool SetResolution(long id, string note) => false;
        public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => Array.Empty<StoredAnalysis>();
        public bool SetAutoResolution(long id, string commit, string note) => false;
        public void Dispose() { }
    }

    [Fact]
    public void Counts_open_resolved_and_unmatched()
    {
        var rows = new[]
        {
            Row(1, "a:1", T0),
            Row(2, "b:1", T0.AddMinutes(1), AnalysisStatus.Resolved, T0.AddMinutes(2)),
            Row(3, "none:1", T0.AddMinutes(3), matched: false),
        };

        var s = StatsComputer.Compute(rows, new StatsQuery());

        s.Total.Should().Be(3);
        s.Open.Should().Be(2);
        s.Resolved.Should().Be(1);
        s.Unmatched.Should().Be(1);
    }

    [Fact]
    public void Recurrence_rate_is_distinct_fingerprints_seen_more_than_once()
    {
        var rows = new[]
        {
            Row(1, "a:1", T0),
            Row(2, "a:1", T0.AddMinutes(1)), // a recurs
            Row(3, "b:1", T0.AddMinutes(2)), // b once
        };

        var s = StatsComputer.Compute(rows, new StatsQuery());

        s.RecurrenceRate.Should().BeApproximately(0.5, 0.001);
        s.TopFailures.Should().Contain(f => f.Fingerprint == "a:1" && f.Count == 2);
    }

    [Fact]
    public void Flags_flaky_when_a_fingerprint_recurs_after_resolution()
    {
        var rows = new[]
        {
            Row(1, "flaky:1", T0, AnalysisStatus.Resolved, resolvedAt: T0.AddHours(1)),
            Row(2, "flaky:1", T0.AddHours(2)), // reappeared after the fix
            Row(3, "stable:1", T0, AnalysisStatus.Resolved, resolvedAt: T0.AddHours(1)),
        };

        var s = StatsComputer.Compute(rows, new StatsQuery());

        s.Flaky.Should().ContainSingle().Which.Fingerprint.Should().Be("flaky:1");
        s.TopFailures.Single(f => f.Fingerprint == "flaky:1").Flaky.Should().BeTrue();
        s.TopFailures.Single(f => f.Fingerprint == "stable:1").Flaky.Should().BeFalse();
    }

    [Fact]
    public void Mean_time_to_resolution_averages_resolved_durations()
    {
        var rows = new[]
        {
            Row(1, "a:1", T0, AnalysisStatus.Resolved, resolvedAt: T0.AddHours(2)),
            Row(2, "b:1", T0, AnalysisStatus.Resolved, resolvedAt: T0.AddHours(4)),
            Row(3, "c:1", T0), // open, no contribution
        };

        var s = StatsComputer.Compute(rows, new StatsQuery());

        s.ResolvedWithTiming.Should().Be(2);
        s.MeanTimeToResolution.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Since_filter_excludes_older_rows()
    {
        var rows = new[]
        {
            Row(1, "old:1", T0),
            Row(2, "new:1", T0.AddDays(10)),
        };

        var s = StatsComputer.Compute(rows, new StatsQuery { Since = T0.AddDays(5) });

        s.Total.Should().Be(1);
        s.TopFailures.Should().ContainSingle().Which.Fingerprint.Should().Be("new:1");
    }

    [Fact]
    public void Repo_filter_scopes_to_one_repository()
    {
        var rows = new[]
        {
            Row(1, "a:1", T0) with { RepoId = "repo-x" },
            Row(2, "b:1", T0) with { RepoId = "repo-y" },
        };

        var s = StatsComputer.Compute(rows, new StatsQuery { RepoId = "repo-x" });

        s.Total.Should().Be(1);
        s.TopFailures.Should().ContainSingle().Which.RuleId.Should().Be("a");
    }

    // --- daily buckets (R32, the sparkline's data) -------------------------------------

    [Fact]
    public void Daily_buckets_include_the_days_with_no_failures()
    {
        // The load-bearing property. A sparkline drawn only from days that had failures is a
        // lie: a quiet week renders as a flat line at the neighbouring values, hiding exactly
        // the gap you wanted to see.
        var now = DateTimeOffset.UtcNow;
        var rows = new[] { Row(1, "a:1", now), Row(2, "a:1", now.AddDays(-3)) };

        var daily = StatsComputer.Compute(rows, new StatsQuery { DailyDays = 5 }).Daily;

        daily.Should().HaveCount(5);
        daily.Select(d => d.Count).Should().Equal(0, 1, 0, 0, 1);
    }

    [Fact]
    public void Daily_buckets_run_oldest_first()
    {
        var daily = StatsComputer.Compute(Array.Empty<StoredAnalysis>(), new StatsQuery { DailyDays = 7 }).Daily;

        daily.Should().HaveCount(7);
        daily.Should().BeInAscendingOrder(d => d.Day);
        daily[^1].Day.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow), "the window ends today");
    }

    [Fact]
    public void Rows_older_than_the_window_are_not_counted()
    {
        var rows = new[] { Row(1, "a:1", DateTimeOffset.UtcNow.AddDays(-40)) };

        var daily = StatsComputer.Compute(rows, new StatsQuery { DailyDays = 7 }).Daily;

        daily.Sum(d => d.Count).Should().Be(0);
    }

    [Fact]
    public void Asking_for_no_window_produces_no_buckets()
    {
        StatsComputer.Compute(Array.Empty<StoredAnalysis>(), new StatsQuery { DailyDays = 0 })
            .Daily.Should().BeEmpty();
    }
}
