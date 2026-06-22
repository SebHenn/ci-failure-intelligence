using CiFail.Core;
using CiFail.Core.Ai;
using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

/// <summary>The R23 safety gate: an AI draft must compile, actually match the log, and not be overbroad.</summary>
public class RuleDraftValidatorTests
{
    private static LogDocument Log(string text) => LogNormalizer.Build("t", text);

    private static AiRuleDraft Draft(string match, string id = "test-rule", string title = "Boom", string fix = "fix it") =>
        new() { Id = id, Ecosystem = "node", Category = "build", Title = title, Match = match, Fix = fix };

    [Fact]
    public void Accepts_a_specific_rule_that_matches_the_log()
    {
        var log = Log("npm ERR! missing script: build\nexiting");
        var result = RuleDraftValidator.Validate(Draft("missing script: (?<script>\\w+)"), log);

        result.Ok.Should().BeTrue();
        result.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        result.MatchedLine.Should().Contain("missing script: build");
        result.Rule.Confidence.Should().Be(RuleDraftValidator.DraftConfidence); // model never asserts its own
    }

    [Fact]
    public void Rejects_a_regex_that_does_not_match_the_log()
    {
        var log = Log("npm ERR! missing script: build");
        var result = RuleDraftValidator.Validate(Draft("totally unrelated pattern"), log);

        result.Ok.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("does not match"));
    }

    [Theory]
    [InlineData(".*")]
    [InlineData(".+")]
    [InlineData("^.*$")]
    [InlineData("")]
    public void Rejects_an_overbroad_pattern(string pattern)
    {
        var result = RuleDraftValidator.Validate(Draft(pattern), Log("anything at all"));

        result.Ok.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Rejects_an_invalid_regex()
    {
        var result = RuleDraftValidator.Validate(Draft("(unclosed group"), Log("boom"));

        result.Ok.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("regex"));
    }

    [Fact]
    public void Rejects_a_catastrophic_backtracking_regex_via_timeout()
    {
        // (a+)+$ against a long non-matching run backtracks exponentially — must be caught, not hang.
        var log = Log(new string('a', 40) + "!");
        var result = RuleDraftValidator.Validate(Draft("(a+)+$"), log);

        result.Ok.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("timed out"));
    }

    [Fact]
    public void Rejects_a_non_kebab_id()
    {
        var log = Log("boom happened");
        var result = RuleDraftValidator.Validate(Draft("boom", id: "Not Kebab!"), log);

        result.Ok.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("kebab-case"));
    }

    [Fact]
    public void Normalizes_an_unknown_ecosystem_to_generic()
    {
        var log = Log("boom happened here");
        var draft = Draft("boom") with { Ecosystem = "not-a-real-ecosystem" };

        var result = RuleDraftValidator.Validate(draft, log);

        result.Rule.Ecosystem.Should().Be("generic");
    }

    [Fact]
    public void A_validated_draft_serializes_to_a_loadable_user_pack()
    {
        // The write path's core mechanic: a validated rule round-trips through the user rules dir.
        var home = Path.Combine(Path.GetTempPath(), "cifail-suggest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, home);
        try
        {
            var log = Log("npm ERR! missing script: build");
            var result = RuleDraftValidator.Validate(Draft("missing script: (?<script>\\w+)", id: "npm-missing-script"), log);
            result.Ok.Should().BeTrue();

            Directory.CreateDirectory(CiFailPaths.UserRulesDir);
            File.WriteAllText(CiFailPaths.SuggestedRulesPath, RulePackLoader.Serialize(new[] { result.Rule }));

            RulePackLoader.LoadAll().Should().Contain(r => r.Id == "npm-missing-script");
            RulePackValidator.ValidateAll().HasErrors.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, null);
            try { if (Directory.Exists(home)) Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
