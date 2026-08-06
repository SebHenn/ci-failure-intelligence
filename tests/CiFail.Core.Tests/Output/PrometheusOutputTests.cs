using System.Globalization;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Output;

/// <summary>
/// The exposition format is unforgiving: one malformed line makes Prometheus reject the whole
/// scrape, not just that series. These pin the parts that are easy to get subtly wrong.
/// </summary>
public class PrometheusOutputTests
{
    private static StatsSnapshot Snapshot(
        IReadOnlyList<CountByKey>? byEcosystem = null,
        IReadOnlyList<FingerprintStat>? top = null,
        TimeSpan? mttr = null) => new()
    {
        Total = 42,
        Open = 30,
        Resolved = 12,
        Unmatched = 5,
        ByEcosystem = byEcosystem ?? Array.Empty<CountByKey>(),
        TopFailures = top ?? Array.Empty<FingerprintStat>(),
        RecurrenceRate = 0.25,
        MeanTimeToResolution = mttr,
        Flaky = Array.Empty<FingerprintStat>(),
    };

    private static FingerprintStat Stat(string fingerprint, int count = 3) => new()
    {
        Fingerprint = fingerprint,
        RuleId = fingerprint.Split(':')[0],
        Count = count,
        OpenCount = 1,
        LastSeen = DateTimeOffset.UnixEpoch,
        Flaky = false,
    };

    [Fact]
    public void Every_metric_is_declared_before_it_is_used()
    {
        var text = PrometheusOutput.Build(Snapshot(
            byEcosystem: new[] { new CountByKey("dotnet", 20) },
            top: new[] { Stat("nuget-nu1101:abc123") }));

        foreach (var name in new[]
                 {
                     "cifail_failures_total", "cifail_failures_open", "cifail_failures_resolved",
                     "cifail_failures_unmatched", "cifail_recurrence_rate", "cifail_flaky_failures",
                     "cifail_failures_by_ecosystem", "cifail_failure_occurrences",
                 })
        {
            text.Should().Contain($"# HELP {name} ");
            text.Should().Contain($"# TYPE {name} gauge");
        }
    }

    [Fact]
    public void Values_use_a_decimal_point_regardless_of_culture()
    {
        // A German or French runner would otherwise emit "0,25" and make the document
        // unparseable — the class of bug that only shows up on someone else's machine.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = PrometheusOutput.Build(Snapshot(mttr: TimeSpan.FromSeconds(90.5)));

            text.Should().Contain("cifail_recurrence_rate 0.25");
            text.Should().Contain("cifail_mean_time_to_resolution_seconds 90.5");
            text.Should().NotContain("0,25");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Mean_time_to_resolution_is_omitted_when_nothing_has_been_resolved()
    {
        // Emitting 0 would read as "we fix everything instantly", which is the opposite of
        // the truth. An absent series is honest.
        PrometheusOutput.Build(Snapshot(mttr: null))
            .Should().NotContain("cifail_mean_time_to_resolution_seconds");
    }

    [Fact]
    public void Label_values_are_escaped()
    {
        var text = PrometheusOutput.Build(Snapshot(
            byEcosystem: new[] { new CountByKey("we\"ird\\one", 1) }));

        text.Should().Contain(@"ecosystem=""we\""ird\\one""");
    }

    [Fact]
    public void Per_fingerprint_series_are_capped()
    {
        // A fingerprint label is unbounded; exporting all of history would quietly hurt
        // whoever scrapes it.
        var many = Enumerable.Range(0, 50).Select(i => Stat($"rule-{i}:hash{i}")).ToList();

        var text = PrometheusOutput.Build(Snapshot(top: many), topFailureLimit: 3);

        text.Split('\n').Count(l => l.StartsWith("cifail_failure_occurrences{", StringComparison.Ordinal))
            .Should().Be(3);
    }

    [Fact]
    public void An_empty_history_still_produces_a_valid_document()
    {
        var text = PrometheusOutput.Build(Snapshot());

        text.Should().Contain("cifail_failures_total 42");
        text.Should().EndWith("\n", "the exposition format wants a trailing newline");
    }
}
