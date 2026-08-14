using CiFail.Core.Analysis;
using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Output;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

/// <summary>
/// R34: a match now carries where it happened and what surrounded it.
///
/// <para>
/// Before this the engine reported a single trimmed line with no position, so the report could
/// not show the "expected X, got Y" a compiler prints under its error, SARIF results were all
/// pinned to line 1, and twelve occurrences of a failure looked exactly like one.
/// </para>
/// </summary>
public class MatchContextTests
{
    private const string Log = """
        Restoring packages
        Compiling 3 files

        src/app.ts(12,5): error TS2345: Argument of type 'string'.
          Type 'string' is not assignable to type 'number'.
        src/other.ts(30,1): error TS2345: Argument of type 'string'.
        Build failed
        """;

    private static RuleEngine Engine() => new(new[]
    {
        new RuleDefinition
        {
            Id = "ts", Ecosystem = "generic", Category = "compile", Title = "TS error",
            Match = @"error TS\d+:", Confidence = 0.9, Fix = "fix it",
        },
    });

    private static RuleMatch Match(int context = 3)
    {
        var log = LogNormalizer.Build("build.log", Log);
        return Engine().Match(log, Ecosystem.Generic, diagnostics: null, contextLines: context)[0];
    }

    [Fact]
    public void The_matched_line_carries_its_1_based_line_number()
    {
        // Line 4 of the log above (1-based), counting the blank line.
        Match().LineNumber.Should().Be(4);
    }

    [Fact]
    public void Context_includes_the_detail_line_underneath_the_error()
    {
        var m = Match();

        m.ContextAfter.Should().Contain(l => l.Contains("is not assignable"),
            "the line under a compiler error is the part that says what to change");
        m.ContextBefore.Should().Contain(l => l.Contains("Compiling 3 files"));
    }

    [Fact]
    public void Context_start_line_lines_up_with_the_block()
    {
        var m = Match();

        m.ContextStartLine.Should().Be(m.LineNumber - m.ContextBefore.Count);
        m.ContextBlock.Should().HaveCount(m.ContextBefore.Count + 1 + m.ContextAfter.Count);
        m.ContextBlock.ElementAt(m.ContextBefore.Count).Should().Be(m.MatchedLine);
    }

    [Fact]
    public void Zero_context_returns_the_matched_line_alone()
    {
        var m = Match(context: 0);

        m.ContextBefore.Should().BeEmpty();
        m.ContextAfter.Should().BeEmpty();
        m.ContextBlock.Should().ContainSingle().Which.Should().Be(m.MatchedLine);
        m.LineNumber.Should().Be(4, "the position is still known even without context");
    }

    [Fact]
    public void Every_occurrence_is_counted_not_just_the_first()
    {
        Match().OccurrenceCount.Should().Be(2);
    }

    /// <summary>A match on the first line must not produce a negative window.</summary>
    [Fact]
    public void A_match_on_the_very_first_line_has_no_leading_context()
    {
        var log = LogNormalizer.Build("t", "error TS2345: boom\ntrailing");

        var m = Engine().Match(log, Ecosystem.Generic)[0];

        m.LineNumber.Should().Be(1);
        m.ContextBefore.Should().BeEmpty();
        m.ContextStartLine.Should().Be(1);
    }

    [Fact]
    public void A_match_on_the_very_last_line_has_no_trailing_context()
    {
        var log = LogNormalizer.Build("t", "leading\nerror TS2345: boom");

        var m = Engine().Match(log, Ecosystem.Generic)[0];

        m.ContextAfter.Should().BeEmpty();
    }

    /// <summary>
    /// The `--json` contract is additive: the new keys are omitted entirely when they carry
    /// nothing, and a full round-trip through the DTO preserves them.
    /// </summary>
    [Fact]
    public void Json_round_trips_the_new_fields()
    {
        var analysis = AnalysisService.CreateDefault().Analyze("build.log", Log);
        var json = AnalysisJson.Serialize(analysis);

        json.Should().Contain("\"LineNumber\"").And.Contain("\"ContextAfter\"")
            .And.Contain("\"OccurrenceCount\"");

        var back = AnalysisJson.FromDto(AnalysisJson.Deserialize(json)!);

        AnalysisJson.Serialize(back).Should().Be(json, "ToDto o FromDto must round-trip exactly");
        back.RootCause!.LineNumber.Should().Be(analysis.RootCause!.LineNumber);
        back.RootCause.ContextAfter.Should().Equal(analysis.RootCause.ContextAfter);
        back.RootCause.OccurrenceCount.Should().Be(analysis.RootCause.OccurrenceCount);
    }

    [Fact]
    public void Json_omits_the_new_fields_when_there_is_nothing_to_say()
    {
        // One line, no context either side, one occurrence.
        var analysis = AnalysisService.CreateDefault()
            .Analyze("t.log", "error TS2345: lonely");

        var json = AnalysisJson.Serialize(analysis);

        json.Should().NotContain("ContextBefore").And.NotContain("ContextAfter")
            .And.NotContain("OccurrenceCount",
                "an older consumer must see exactly the document it saw before");
    }

    /// <summary>
    /// The stored excerpt is the only record of a failure once the CI log is gone, and it used to
    /// be the matched line by itself — so a failure could never be re-read with any of the output
    /// around it.
    /// </summary>
    [Fact]
    public void The_persisted_excerpt_keeps_the_context_block()
    {
        var analysis = AnalysisService.CreateDefault().Analyze("build.log", Log);
        var excerpt = string.Join('\n', analysis.RootCause!.ContextBlock);

        excerpt.Should().Contain("is not assignable")
            .And.Contain("error TS2345");
    }
}
