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
}
