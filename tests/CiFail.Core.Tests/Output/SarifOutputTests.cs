using System.Text.Json;
using CiFail.Core.Output;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Output;

/// <summary>Structural assertions on the SARIF 2.1.0 report (R24).</summary>
public class SarifOutputTests
{
    private static AnalysisJson.AnalysisDto Matched(
        string source, string ruleId, string title, double confidence, string fingerprint) => new()
    {
        Source = source,
        Ecosystem = "node",
        Matched = true,
        Fingerprint = fingerprint,
        RootCause = new AnalysisJson.MatchDto
        {
            RuleId = ruleId,
            Title = title,
            Category = "dependency",
            Confidence = confidence,
            MatchedLine = "npm ERR! 404",
            Fix = "install the package",
            Docs = "https://example.com/docs",
        },
    };

    private static AnalysisJson.AnalysisDto Unmatched(string source, string fingerprint) => new()
    {
        Source = source,
        Ecosystem = "generic",
        Matched = false,
        Fingerprint = fingerprint,
    };

    [Fact]
    public void Emits_valid_sarif_envelope()
    {
        var json = SarifOutput.Build(new[] { Matched("build.log", "npm-404", "Package not found", 0.9, "npm-404:abc") });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("$schema").GetString().Should().Contain("sarif-2.1.0");
        root.GetProperty("version").GetString().Should().Be("2.1.0");
        root.GetProperty("runs").GetArrayLength().Should().Be(1);

        var driver = root.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
        driver.GetProperty("name").GetString().Should().Be("cifail");
        driver.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Dedupes_rules_and_resolves_ruleIndex()
    {
        var dtos = new[]
        {
            Matched("a.log", "npm-404", "Package not found", 0.9, "npm-404:a"),
            Matched("b.log", "npm-404", "Package not found", 0.9, "npm-404:b"),
            Matched("c.log", "nu-1101", "NuGet restore", 0.7, "nu-1101:c"),
        };

        var json = SarifOutput.Build(dtos);
        using var doc = JsonDocument.Parse(json);
        var run = doc.RootElement.GetProperty("runs")[0];

        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        rules.GetArrayLength().Should().Be(2, "the duplicate rule id collapses into one descriptor");

        var results = run.GetProperty("results");
        results.GetArrayLength().Should().Be(3, "one result per analysis unit");

        // Every result's ruleIndex must point at a descriptor with the same id.
        foreach (var result in results.EnumerateArray())
        {
            var idx = result.GetProperty("ruleIndex").GetInt32();
            var ruleId = result.GetProperty("ruleId").GetString();
            rules[idx].GetProperty("id").GetString().Should().Be(ruleId);
        }
    }

    [Fact]
    public void Maps_confidence_to_level()
    {
        var json = SarifOutput.Build(new[]
        {
            Matched("hi.log", "r-hi", "High", 0.9, "r-hi:1"),
            Matched("mid.log", "r-mid", "Mid", 0.7, "r-mid:1"),
            Matched("lo.log", "r-lo", "Low", 0.3, "r-lo:1"),
        });
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        results[0].GetProperty("level").GetString().Should().Be("error");
        results[1].GetProperty("level").GetString().Should().Be("warning");
        results[2].GetProperty("level").GetString().Should().Be("note");
    }

    [Fact]
    public void Carries_partial_fingerprints()
    {
        var json = SarifOutput.Build(new[] { Matched("build.log", "npm-404", "x", 0.9, "npm-404:deadbeef") });
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0];

        result.GetProperty("partialFingerprints").GetProperty("cifailFingerprint/v1").GetString()
            .Should().Be("npm-404:deadbeef");
    }

    [Fact]
    public void Unmatched_unit_gets_a_synthetic_note_rule()
    {
        var json = SarifOutput.Build(new[] { Unmatched("mystery.log", "unmatched:0") });
        using var doc = JsonDocument.Parse(json);
        var run = doc.RootElement.GetProperty("runs")[0];

        var result = run.GetProperty("results")[0];
        result.GetProperty("ruleId").GetString().Should().Be("cifail-unmatched");
        result.GetProperty("level").GetString().Should().Be("note");
    }

    [Fact]
    public void Report_expanded_test_source_uses_the_file_part_as_the_artifact_uri()
    {
        // R17 expands a JUnit report into one unit per failing test: source = "<file>::<FullName>".
        var dtos = new[]
        {
            Matched("tests/results.xml::Suite.TestA", "r", "A", 0.9, "r:a"),
            Matched("tests/results.xml::Suite.TestB", "r", "B", 0.9, "r:b"),
            Unmatched("tests/results.xml::Suite.TestC", "u:c"),
        };

        var json = SarifOutput.Build(dtos);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");

        results.GetArrayLength().Should().Be(3);
        foreach (var result in results.EnumerateArray())
        {
            var uri = result.GetProperty("locations")[0]
                .GetProperty("physicalLocation").GetProperty("artifactLocation")
                .GetProperty("uri").GetString();
            uri.Should().Be("tests/results.xml", "the test name is stripped off the artifact uri");
        }
    }

    [Fact]
    public void Stdin_source_relativizes_to_a_placeholder()
    {
        var json = SarifOutput.Build(new[] { Matched("stdin", "r", "x", 0.9, "r:1") });
        using var doc = JsonDocument.Parse(json);
        var uri = doc.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("locations")[0].GetProperty("physicalLocation")
            .GetProperty("artifactLocation").GetProperty("uri").GetString();

        uri.Should().Be("stdin");
    }
}
