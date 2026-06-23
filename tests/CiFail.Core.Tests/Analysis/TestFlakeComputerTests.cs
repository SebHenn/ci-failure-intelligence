using CiFail.Core.Analysis;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

/// <summary>Per-test flakiness aggregation over report-expanded rows (R26).</summary>
public class TestFlakeComputerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static StoredAnalysis Row(
        long id, string source, DateTimeOffset at,
        string status = AnalysisStatus.Open, DateTimeOffset? resolvedAt = null, string? repoId = null) => new()
    {
        Id = id,
        AnalyzedAt = at,
        Source = source,
        Ecosystem = "dotnet",
        RuleId = "r",
        Matched = true,
        Fingerprint = $"r:{id}",
        LogHash = "h",
        Excerpt = "x",
        Status = status,
        ResolvedAt = resolvedAt,
        RepoId = repoId,
    };

    [Fact]
    public void Only_report_expanded_rows_count_as_tests()
    {
        var rows = new[]
        {
            Row(1, "results.xml::Suite.TestA", T0),
            Row(2, "build.log", T0), // a plain log, not a test row
        };

        var s = TestFlakeComputer.Compute(rows, new TestStatsQuery());

        s.TotalTestFailures.Should().Be(1);
        s.DistinctTests.Should().Be(1);
        s.Tests.Should().ContainSingle().Which.FullName.Should().Be("Suite.TestA");
    }

    [Fact]
    public void A_repeatedly_failing_test_ranks_above_a_one_off()
    {
        var rows = new[]
        {
            Row(1, "r.xml::Flapper", T0),
            Row(2, "r.xml::Flapper", T0.AddDays(1)),
            Row(3, "r.xml::Flapper", T0.AddDays(2)),
            Row(4, "r.xml::OneOff", T0),
        };

        var s = TestFlakeComputer.Compute(rows, new TestStatsQuery());

        s.Tests[0].FullName.Should().Be("Flapper");
        s.Tests[0].FailureCount.Should().Be(3);
        s.Tests[0].DistinctDays.Should().Be(3);
    }

    [Fact]
    public void A_test_resolved_then_failed_again_is_flaky()
    {
        var rows = new[]
        {
            Row(1, "r.xml::Shaky", T0, AnalysisStatus.Resolved, resolvedAt: T0.AddHours(1)),
            Row(2, "r.xml::Shaky", T0.AddDays(1)), // recurred after the resolution
        };

        var s = TestFlakeComputer.Compute(rows, new TestStatsQuery());

        var shaky = s.Tests.Should().ContainSingle().Subject;
        shaky.Flaky.Should().BeTrue();
        s.FlakyCount.Should().Be(1);
    }

    [Fact]
    public void A_cleanly_resolved_test_is_not_flaky()
    {
        var rows = new[]
        {
            Row(1, "r.xml::Stable", T0, AnalysisStatus.Resolved, resolvedAt: T0.AddHours(1)),
        };

        var s = TestFlakeComputer.Compute(rows, new TestStatsQuery());

        s.Tests.Single().Flaky.Should().BeFalse();
        s.Tests.Single().OpenCount.Should().Be(0);
        s.FlakyCount.Should().Be(0);
    }

    [Fact]
    public void Handles_cpp_style_names_with_colons_in_the_test_name()
    {
        // The file path has no "::"; the test name does — split on the FIRST "::".
        var rows = new[] { Row(1, "ctest.xml::suite::Math::adds", T0) };

        var s = TestFlakeComputer.Compute(rows, new TestStatsQuery());

        s.Tests.Single().FullName.Should().Be("suite::Math::adds");
    }

    [Fact]
    public void Filters_by_since_and_repo()
    {
        var rows = new[]
        {
            Row(1, "r.xml::T", T0, repoId: "A"),
            Row(2, "r.xml::T", T0.AddDays(10), repoId: "A"),
            Row(3, "r.xml::T", T0.AddDays(10), repoId: "B"),
        };

        TestFlakeComputer.Compute(rows, new TestStatsQuery { Since = T0.AddDays(5) })
            .TotalTestFailures.Should().Be(2);
        TestFlakeComputer.Compute(rows, new TestStatsQuery { RepoId = "A" })
            .TotalTestFailures.Should().Be(2);
    }

    [Fact]
    public void Json_round_trips()
    {
        var snapshot = TestFlakeComputer.Compute(
            new[] { Row(1, "r.xml::T", T0), Row(2, "r.xml::T", T0.AddDays(1)) },
            new TestStatsQuery());

        var back = TestStatsJson.FromDto(TestStatsJson.ToDto(snapshot));

        back.Should().BeEquivalentTo(snapshot);
    }
}
