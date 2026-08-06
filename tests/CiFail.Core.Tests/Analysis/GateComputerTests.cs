using CiFail.Core.Analysis;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

/// <summary>
/// <see cref="GateComputer"/> is the whole decision behind <c>cifail gate</c>: it says which
/// failures are new. Everything else in the command is I/O around this.
/// </summary>
public class GateComputerTests
{
    private static ObservedFailure Seen(string fingerprint, string source = "build.log", string? title = "A failure")
        => new(fingerprint, source, fingerprint.Split(':')[0], title);

    private static IReadOnlySet<string> Baseline(params string[] fingerprints)
        => new HashSet<string>(fingerprints, StringComparer.Ordinal);

    [Fact]
    public void An_empty_baseline_makes_every_failure_new()
    {
        var result = GateComputer.Evaluate(
            new[] { Seen("a:1"), Seen("b:2") },
            Baseline());

        result.New.Select(f => f.Fingerprint).Should().Equal("a:1", "b:2");
        result.Known.Should().BeEmpty();
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void A_baselined_failure_does_not_fail_the_gate()
    {
        var result = GateComputer.Evaluate(new[] { Seen("a:1") }, Baseline("a:1"));

        result.New.Should().BeEmpty();
        result.Known.Should().ContainSingle().Which.Fingerprint.Should().Be("a:1");
        result.Passed.Should().BeTrue("the backlog is exactly what a gate must tolerate");
    }

    [Fact]
    public void One_new_failure_among_known_ones_still_fails_the_gate()
    {
        var result = GateComputer.Evaluate(
            new[] { Seen("known:1"), Seen("fresh:2"), Seen("known:3") },
            Baseline("known:1", "known:3"));

        result.New.Should().ContainSingle().Which.Fingerprint.Should().Be("fresh:2");
        result.Known.Should().HaveCount(2);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void No_failures_at_all_passes()
    {
        var result = GateComputer.Evaluate(Array.Empty<ObservedFailure>(), Baseline("a:1"));

        result.New.Should().BeEmpty();
        result.Known.Should().BeEmpty();
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void The_same_failure_in_several_places_is_one_finding_listing_all_of_them()
    {
        // A test report expands into one unit per failing test, so the same root cause
        // routinely arrives several times. Reporting it three times would be noise, and
        // baselining it three times would put three identical lines in a reviewed file.
        var result = GateComputer.Evaluate(
            new[]
            {
                Seen("a:1", "tests.trx::One"),
                Seen("a:1", "tests.trx::Two"),
                Seen("a:1", "tests.trx::Three"),
            },
            Baseline());

        var finding = result.New.Should().ContainSingle().Subject;
        finding.Count.Should().Be(3);
        finding.Sources.Should().Equal("tests.trx::One", "tests.trx::Two", "tests.trx::Three");
    }

    [Fact]
    public void Findings_keep_the_order_they_were_seen_in()
    {
        // So the report reads in the same order as the log it came from.
        var result = GateComputer.Evaluate(
            new[] { Seen("z:1"), Seen("a:2"), Seen("m:3") },
            Baseline());

        result.New.Select(f => f.Fingerprint).Should().Equal("z:1", "a:2", "m:3");
    }

    [Fact]
    public void Baseline_entries_that_did_not_occur_are_reported_as_stale()
    {
        var result = GateComputer.Evaluate(
            new[] { Seen("still-here:1") },
            Baseline("still-here:1", "gone:2", "also-gone:3"));

        result.Stale.Should().Equal("also-gone:3", "gone:2");
        result.Passed.Should().BeTrue("a stale entry is advisory — it can never fail the gate");
    }

    [Fact]
    public void An_unmatched_failure_is_gated_like_any_other()
    {
        // The most important thing to gate on is a failure nothing explains yet.
        var result = GateComputer.Evaluate(
            new[] { new ObservedFailure("unknown:abc123", "build.log", "unknown", null) },
            Baseline());

        var finding = result.New.Should().ContainSingle().Subject;
        finding.RuleId.Should().Be("unknown");
        finding.Title.Should().BeNull();
    }
}
