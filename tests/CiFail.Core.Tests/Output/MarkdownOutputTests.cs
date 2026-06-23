using CiFail.Core.Output;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Output;

/// <summary>The Markdown report mirrors the console wording — plain, not raw scores (R24).</summary>
public class MarkdownOutputTests
{
    [Fact]
    public void Renders_a_matched_failure_with_words_not_scores()
    {
        var dto = new AnalysisJson.AnalysisDto
        {
            Source = "build.log",
            Ecosystem = "node",
            Matched = true,
            Fingerprint = "npm-404:abc",
            RootCause = new AnalysisJson.MatchDto
            {
                RuleId = "npm-404",
                Title = "Package not found",
                Category = "dependency",
                Confidence = 0.9,
                MatchedLine = "npm ERR! 404 Not Found - left-pad",
                Fix = "Check the package name and registry.",
                Docs = "https://docs.npmjs.com/cli/errors",
            },
        };

        var md = MarkdownOutput.Build(new[] { dto });

        md.Should().Contain("## cifail analysis");
        md.Should().Contain("### Package not found");
        md.Should().Contain("**Confidence:** high");
        md.Should().NotContain("0.9");
        md.Should().Contain("npm ERR! 404 Not Found - left-pad");
        md.Should().Contain("Check the package name and registry.");
        md.Should().Contain("https://docs.npmjs.com/cli/errors");
        md.Should().EndWith("\n");
    }

    [Fact]
    public void Renders_an_unmatched_failure()
    {
        var dto = new AnalysisJson.AnalysisDto
        {
            Source = "mystery.log",
            Ecosystem = "generic",
            Matched = false,
            Fingerprint = "unmatched:0",
        };

        var md = MarkdownOutput.Build(new[] { dto });

        md.Should().Contain("No known rule matched");
        md.Should().Contain("mystery.log");
        md.Should().Contain("suggest-rule");
    }

    [Fact]
    public void Summarizes_a_batch()
    {
        var dtos = new[]
        {
            new AnalysisJson.AnalysisDto
            {
                Source = "a.log", Matched = true, Fingerprint = "r:a",
                RootCause = new AnalysisJson.MatchDto { RuleId = "r", Title = "A", Category = "c", Confidence = 0.9, Fix = "f" },
            },
            new AnalysisJson.AnalysisDto { Source = "b.log", Matched = false, Fingerprint = "u:b" },
        };

        var md = MarkdownOutput.Build(dtos);

        md.Should().Contain("Analyzed **2** logs");
        md.Should().Contain("matched a known failure in **1**");
    }

    [Fact]
    public void Renders_similar_failures_and_ai_suggestion()
    {
        var dto = new AnalysisJson.AnalysisDto
        {
            Source = "build.log",
            Matched = true,
            Fingerprint = "r:a",
            RootCause = new AnalysisJson.MatchDto { RuleId = "r", Title = "T", Category = "c", Confidence = 0.7, Fix = "fix it" },
            SimilarFailures =
            {
                new AnalysisJson.SimilarDto { Id = 42, AnalyzedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), Resolution = "bumped dep" },
            },
            Ai = new AnalysisJson.AiDto { Model = "llama3", RootCause = "It broke because X.", Fix = "Do Y." },
        };

        var md = MarkdownOutput.Build(new[] { dto });

        md.Should().Contain("**Seen before**");
        md.Should().Contain("#42");
        md.Should().Contain("bumped dep");
        md.Should().Contain("**AI suggestion** (llama3)");
        md.Should().Contain("It broke because X.");
        md.Should().Contain("Do Y.");
    }
}
