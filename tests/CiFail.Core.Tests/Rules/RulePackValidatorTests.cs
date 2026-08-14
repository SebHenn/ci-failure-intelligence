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

    /// <summary>
    /// The real shape of the bug this check exists for: <c>file</c> is captured, so a naive
    /// "does the group exist anywhere in the pattern" check passes — but a log matching the
    /// second alternative renders <c>{file}</c> literally. Eleven shipped rules were in exactly
    /// this state, split between the <c>2</c>-suffix workaround below and branches that carry no
    /// captures at all.
    /// </summary>
    [Fact]
    public void Placeholder_missing_from_one_alternation_branch_is_an_error()
    {
        const string yaml = """
        - id: half-captured
          ecosystem: generic
          title: Half captured
          match: "error at (?<file>\\S+)|something else went wrong"
          confidence: 0.5
          fix: "Look at {file}."
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("half.yaml", RulePackTier.User, yaml),
        });

        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().ContainSingle(d => d.Message.Contains("{file}"))
            .Which.Message.Should().Contain("something else went wrong");
    }

    /// <summary>Duplicate group names across branches are the fix, so they must lint clean.</summary>
    [Fact]
    public void Duplicate_group_names_across_branches_satisfy_the_placeholder_check()
    {
        const string yaml = """
        - id: both-captured
          ecosystem: generic
          title: Both captured
          match: "error at (?<file>\\S+)|failure in (?<file>\\S+)"
          confidence: 0.5
          fix: "Look at {file}."
          docs: https://example.com/docs
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("both.yaml", RulePackTier.User, yaml),
        });

        result.Diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// A <c>|</c> inside a group or a character class is not an alternation branch. Splitting the
    /// pattern with a regex would get all of these wrong.
    /// </summary>
    [Theory]
    [InlineData(@"a|b", 2)]
    [InlineData(@"(?:a|b)c", 1)]
    [InlineData(@"[a|b]c", 1)]
    [InlineData(@"a\|b", 1)]
    [InlineData(@"(a|b)|c", 2)]
    [InlineData(@"[^|]+|x", 2)]
    public void Top_level_alternation_split_ignores_grouped_classed_and_escaped_pipes(
        string pattern, int expected)
    {
        RulePackValidator.SplitTopLevelAlternation(pattern).Should().HaveCount(expected);
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
