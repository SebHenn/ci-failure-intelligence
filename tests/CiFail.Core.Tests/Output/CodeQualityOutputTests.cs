using System.Text.Json;
using CiFail.Core.Output;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Output;

/// <summary>
/// GitLab Code Quality report (R40). GitLab ingests SARIF only from its own security scanners,
/// so cifail's SARIF was invisible there; this is the equivalent native surface.
/// </summary>
public class CodeQualityOutputTests
{
    private static AnalysisJson.AnalysisDto Matched(
        string source = "build.log", string ruleId = "npm-404", double confidence = 0.9,
        string? severity = null, int? line = 42) => new()
    {
        Source = source,
        Ecosystem = "node",
        Matched = true,
        Fingerprint = $"{ruleId}:abc123",
        RootCause = new AnalysisJson.MatchDto
        {
            RuleId = ruleId,
            Title = "Package not found",
            Category = "dependency",
            Confidence = confidence,
            MatchedLine = "npm ERR! 404",
            Fix = "Install the package.\nThen re-run the build.",
            Severity = severity,
            LineNumber = line,
        },
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Emits_a_json_array_even_when_there_is_nothing_to_report()
    {
        var root = Parse(CodeQualityOutput.Build(Array.Empty<AnalysisJson.AnalysisDto>()));

        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Carries_the_fields_gitlab_requires()
    {
        var issue = Parse(CodeQualityOutput.Build(new[] { Matched() }))[0];

        issue.GetProperty("description").GetString().Should().Contain("Package not found");
        issue.GetProperty("checkName").GetString().Should().Be("npm-404");
        issue.GetProperty("severity").GetString().Should().Be("major");
        issue.GetProperty("location").GetProperty("path").GetString().Should().Be("build.log");
        issue.GetProperty("location").GetProperty("lines").GetProperty("begin").GetInt32().Should().Be(42);
    }

    /// <summary>
    /// GitLab dedupes findings across pipelines on this value, so it has to be cifail's own
    /// fingerprint — which is stable across runs by design.
    /// </summary>
    [Fact]
    public void Uses_the_cifail_fingerprint_for_deduplication()
    {
        var issue = Parse(CodeQualityOutput.Build(new[] { Matched() }))[0];

        issue.GetProperty("fingerprint").GetString().Should().Be("npm-404:abc123");
    }

    [Theory]
    [InlineData(0.9, null, "major")]
    [InlineData(0.7, null, "minor")]
    [InlineData(0.4, null, "info")]
    [InlineData(0.9, "note", "info")]     // severity overrides the confidence bucket (R36)
    [InlineData(0.4, "error", "major")]
    public void Severity_matches_the_sarif_mapping(double confidence, string? severity, string expected)
    {
        var issue = Parse(CodeQualityOutput.Build(new[] { Matched(confidence: confidence, severity: severity) }))[0];

        issue.GetProperty("severity").GetString().Should().Be(expected);
    }

    [Fact]
    public void An_unmatched_failure_is_reported_as_info()
    {
        var dto = new AnalysisJson.AnalysisDto
        {
            Source = "build.log", Ecosystem = "generic", Matched = false, Fingerprint = "unknown:abc",
        };

        var issue = Parse(CodeQualityOutput.Build(new[] { dto }))[0];

        issue.GetProperty("severity").GetString().Should().Be("info");
        issue.GetProperty("checkName").GetString().Should().Be("cifail-unmatched");
        issue.GetProperty("location").GetProperty("lines").GetProperty("begin").GetInt32().Should().Be(1);
    }

    /// <summary>A report-expanded test source is `file::TestName`; only the file half resolves.</summary>
    [Fact]
    public void A_test_source_contributes_only_its_file_part()
    {
        var issue = Parse(CodeQualityOutput.Build(new[] { Matched(source: "tests/results.xml::Suite.Test") }))[0];

        issue.GetProperty("location").GetProperty("path").GetString().Should().Be("tests/results.xml");
    }

    [Fact]
    public void Backslash_paths_are_normalized_for_gitlab()
    {
        var issue = Parse(CodeQualityOutput.Build(new[] { Matched(source: @"logs\build.log") }))[0];

        issue.GetProperty("location").GetProperty("path").GetString().Should().Be("logs/build.log");
    }

    /// <summary>The widget shows one line, and fix text is a paragraph.</summary>
    [Fact]
    public void The_description_is_a_single_line()
    {
        var issue = Parse(CodeQualityOutput.Build(new[] { Matched() }))[0];

        issue.GetProperty("description").GetString().Should().NotContain("\n");
    }
}
