using CiFail.Core.Ingest;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ingest;

/// <summary>
/// R37: a cap on how much log cifail will hold in memory at once.
///
/// <para>
/// Nothing bounded this before. A LogDocument keeps the raw text, the normalized text and a line
/// array, and Scrub makes seven more full-text passes on the similarity path — so a 200 MB log
/// meant gigabytes of large-object-heap allocation, and past ~1e9 characters .NET cannot hold the
/// string at all.
/// </para>
/// </summary>
public class LogWindowTests
{
    private static string Log(int lines, string filler = "progress output") =>
        string.Join('\n', Enumerable.Range(0, lines).Select(i => $"{filler} line {i}"));

    [Fact]
    public void A_log_within_the_cap_is_untouched()
    {
        var log = Log(100);

        LogNormalizer.Window(log).Should().Be(log);
    }

    /// <summary>
    /// The two ends are what matter: in CI the failure is at the end (the compiler error, the
    /// assertion, the exit code) while the beginning carries the tool versions and command line
    /// the ecosystem detector keys on. Cutting from either end alone throws away one or the other.
    /// </summary>
    [Fact]
    public void An_oversized_log_keeps_its_head_and_its_tail()
    {
        var log = "FIRST LINE MARKER\n" + Log(50_000) + "\nerror: THE ACTUAL FAILURE";

        var windowed = LogNormalizer.Window(log, maxCharacters: 4000);

        windowed.Should().StartWith("FIRST LINE MARKER");
        windowed.Should().EndWith("error: THE ACTUAL FAILURE");
        windowed.Length.Should().BeLessThan(log.Length);
    }

    [Fact]
    public void The_omission_is_marked_rather_than_silent()
    {
        var windowed = LogNormalizer.Window(Log(50_000), maxCharacters: 4000);

        windowed.Should().Contain(LogNormalizer.ElisionMarker,
            "a silently truncated log is one where you cannot tell why a rule didn't fire");
    }

    [Fact]
    public void The_window_respects_the_budget()
    {
        const int budget = 4000;

        var windowed = LogNormalizer.Window(Log(50_000), maxCharacters: budget);

        windowed.Length.Should().BeLessThanOrEqualTo(budget);
    }

    [Fact]
    public void Neither_half_is_cut_mid_line()
    {
        var windowed = LogNormalizer.Window(Log(50_000), maxCharacters: 4000);
        var lines = windowed.Split('\n');

        // Every retained line is a whole one from the source (or the marker itself).
        foreach (var line in lines.Where(l => l.Length > 0 && l != LogNormalizer.ElisionMarker))
            line.Should().MatchRegex(@"^progress output line \d+$");
    }

    /// <summary>The failure has to survive windowing, or the cap has broken the product.</summary>
    [Fact]
    public void A_rule_still_matches_a_failure_at_the_end_of_an_oversized_log()
    {
        var raw = Log(400_000) + "\nerror NU1101: Unable to find package Newtonsoft.Jsn.";

        var analysis = CiFail.Core.Analysis.AnalysisService.CreateDefault().Analyze("big.log", raw);

        analysis.RootCause.Should().NotBeNull();
        analysis.RootCause!.Rule.Id.Should().Be("nuget-nu1101");
    }

    [Fact]
    public void An_oversized_log_does_not_blow_up_the_document()
    {
        var raw = Log(400_000);

        var doc = LogNormalizer.Build("big.log", raw);

        doc.NormalizedText.Length.Should().BeLessThanOrEqualTo(LogNormalizer.MaxCharacters);
        doc.RawText.Length.Should().BeLessThanOrEqualTo(LogNormalizer.MaxCharacters,
            "the document must not pin the original string it chose not to analyze");
    }

    [Theory]
    [InlineData("")]
    [InlineData("one line")]
    public void Degenerate_input_is_returned_as_is(string input) =>
        LogNormalizer.Window(input, maxCharacters: 10).Should().Be(input);
}
