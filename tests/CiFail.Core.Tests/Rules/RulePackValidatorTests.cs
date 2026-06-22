using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

public class RulePackValidatorTests
{
    [Fact]
    public void Shipped_embedded_packs_validate_clean()
    {
        // Validate just the embedded tier (point user dir at a path that doesn't exist).
        var result = RulePackValidator.ValidateAll(userRulesDir: Path.Combine(Path.GetTempPath(), "cifail-no-such-dir"));

        result.HasErrors.Should().BeFalse(
            because: "the shipped rule packs must always lint clean: " +
                     string.Join("; ", result.Diagnostics.Select(d => $"{d.Source}:{d.Message}")));
        result.RuleCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Reports_malformed_regex_missing_match_and_bad_confidence()
    {
        const string yaml = """
        - id: bad-regex
          ecosystem: generic
          title: Bad
          match: "error (?<unterminated"
          confidence: 0.5
          fix: x
        - id: no-match
          ecosystem: generic
          title: No match
          confidence: 2.5
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("bad.yaml", RulePackTier.User, yaml),
        });

        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().Contain(d => d.RuleId == "bad-regex" && d.Message.Contains("regex"));
        result.Diagnostics.Should().Contain(d => d.RuleId == "no-match" && d.Message.Contains("match"));
        result.Diagnostics.Should().Contain(d => d.RuleId == "no-match" && d.Message.Contains("confidence"));
    }

    [Fact]
    public void Duplicate_id_in_same_tier_is_an_error()
    {
        const string yaml = """
        - id: dup
          ecosystem: generic
          title: One
          match: "a"
          confidence: 0.5
          fix: x
        - id: dup
          ecosystem: generic
          title: Two
          match: "b"
          confidence: 0.5
          fix: y
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("pack.yaml", RulePackTier.User, yaml),
        });

        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.RuleId == "dup" && d.Message.Contains("duplicate"));
    }

    [Fact]
    public void User_override_of_embedded_id_is_a_warning_not_an_error()
    {
        var ruleYaml = """
        - id: shared
          ecosystem: generic
          title: T
          match: "a"
          confidence: 0.5
          fix: x
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("embedded.yaml", RulePackTier.Embedded, ruleYaml),
            new RulePackDocument("user.yaml", RulePackTier.User, ruleYaml),
        });

        result.HasErrors.Should().BeFalse();
        result.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning && d.RuleId == "shared" && d.Message.Contains("override"));
    }

    [Fact]
    public void Malformed_yaml_is_reported_not_thrown()
    {
        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("broken.yaml", RulePackTier.User, "- id: x\n  match: [unclosed"),
        });

        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().Contain(d => d.Message.Contains("YAML"));
    }
}
