using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Output;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

/// <summary>
/// R36: the rule schema gained <c>severity</c>, <c>notMatch</c>, <c>requires</c>,
/// <c>ecosystems</c> and <c>enabled</c>.
///
/// <para>
/// Every one is optional, and a pack written before they existed must behave exactly as it did —
/// which is the property most of these assert.
/// </para>
/// </summary>
public class RuleSchemaTests
{
    private static RuleDefinition Rule(string id = "r", string match = "boom") => new()
    {
        Id = id, Ecosystem = "generic", Category = "test", Title = id,
        Match = match, Confidence = 0.8, Fix = "fix it",
    };

    private static IReadOnlyList<RuleMatch> Run(RuleDefinition rule, string log, Ecosystem eco = Ecosystem.Generic) =>
        new RuleEngine(new[] { rule }).Match(LogNormalizer.Build("t", log), eco);

    // ---- enabled -------------------------------------------------------------------------

    [Fact]
    public void A_rule_defaults_to_enabled()
    {
        Run(Rule(), "boom happened").Should().ContainSingle();
    }

    [Fact]
    public void A_disabled_rule_never_fires()
    {
        var rule = Rule();
        rule.Enabled = false;

        Run(rule, "boom happened").Should().BeEmpty();
    }

    /// <summary>
    /// The point of the field: silencing a shipped rule. A user pack redefining the id wins, so a
    /// stub with `enabled: false` switches it off — previously impossible, because the validator
    /// rejects `confidence: 0` and the only workaround was overriding with a pattern matching
    /// nothing.
    /// </summary>
    [Fact]
    public void A_disabled_stub_can_lint_clean_without_a_match_or_fix()
    {
        const string yaml = """
        - id: nuget-nu1101
          enabled: false
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("off.yaml", RulePackTier.User, yaml),
        });

        result.HasErrors.Should().BeFalse(
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    // ---- notMatch ------------------------------------------------------------------------

    [Fact]
    public void NotMatch_suppresses_the_rule_when_it_also_matches()
    {
        var rule = Rule();
        rule.NotMatch = "known flake";

        Run(rule, "boom happened").Should().ContainSingle();
        Run(rule, "boom happened\nthis is a known flake").Should().BeEmpty();
    }

    // ---- requires ------------------------------------------------------------------------

    [Fact]
    public void Requires_holds_the_rule_back_until_the_second_pattern_appears()
    {
        var rule = Rule();
        rule.Requires = "Running tests";

        Run(rule, "boom happened").Should().BeEmpty();
        Run(rule, "Running tests\nboom happened").Should().ContainSingle();
    }

    /// <summary>
    /// A guard that cannot be evaluated must leave the rule quiet. Failing open on a broken
    /// <c>notMatch</c> would let through exactly the match it exists to suppress.
    /// </summary>
    [Fact]
    public void An_invalid_guard_keeps_the_rule_quiet_rather_than_firing_anyway()
    {
        var suppressed = Rule();
        suppressed.NotMatch = "(unclosed";

        var gated = Rule();
        gated.Requires = "(unclosed";

        Run(suppressed, "boom happened").Should().BeEmpty();
        Run(gated, "boom happened").Should().BeEmpty();
    }

    [Fact]
    public void An_invalid_guard_is_a_validation_error()
    {
        const string yaml = """
        - id: bad-guard
          ecosystem: generic
          title: Bad guard
          match: "boom"
          notMatch: "(unclosed"
          confidence: 0.5
          fix: x
          docs: https://example.com
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("g.yaml", RulePackTier.User, yaml),
        });

        result.Diagnostics.Should().Contain(d => d.Message.Contains("notMatch"));
    }

    // ---- ecosystems ----------------------------------------------------------------------

    [Fact]
    public void A_rule_can_declare_more_than_one_ecosystem()
    {
        var rule = Rule();
        rule.Ecosystem = "node";
        rule.Ecosystems = new List<string> { "go" };

        Run(rule, "boom", Ecosystem.Node).Should().ContainSingle();
        Run(rule, "boom", Ecosystem.Go).Should().ContainSingle();
        Run(rule, "boom", Ecosystem.Python).Should().BeEmpty();
    }

    [Fact]
    public void A_rule_with_no_ecosystems_list_behaves_exactly_as_before()
    {
        var rule = Rule();
        rule.Ecosystem = "node";

        rule.AllEcosystems.Should().Equal("node");
        Run(rule, "boom", Ecosystem.Node).Should().ContainSingle();
        Run(rule, "boom", Ecosystem.Python).Should().BeEmpty();
    }

    // ---- severity ------------------------------------------------------------------------

    [Fact]
    public void Severity_overrides_the_confidence_derived_sarif_level()
    {
        // High confidence would map to "error"; the rule says it is only a note.
        ReportFormatting.SarifLevel(0.9, RuleSeverity.Note).Should().Be("note");

        // Low confidence would map to "note"; the rule says it is fatal.
        ReportFormatting.SarifLevel(0.4, RuleSeverity.Error).Should().Be("error");
    }

    [Fact]
    public void Without_a_severity_the_level_still_comes_from_confidence()
    {
        ReportFormatting.SarifLevel(0.9, null).Should().Be(ReportFormatting.SarifLevel(0.9));
        ReportFormatting.SarifLevel(0.4, "  ").Should().Be(ReportFormatting.SarifLevel(0.4));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("warning")]
    [InlineData("note")]
    [InlineData(null)]
    public void Valid_severities_are_accepted(string? severity) =>
        RuleSeverity.IsValid(severity).Should().BeTrue();

    [Fact]
    public void An_unknown_severity_is_a_validation_error()
    {
        const string yaml = """
        - id: bad-sev
          ecosystem: generic
          title: Bad severity
          match: "boom"
          severity: catastrophic
          confidence: 0.5
          fix: x
          docs: https://example.com
        """;

        var result = RulePackValidator.Validate(new[]
        {
            new RulePackDocument("s.yaml", RulePackTier.User, yaml),
        });

        result.HasErrors.Should().BeTrue();
        result.Diagnostics.Should().Contain(d => d.Message.Contains("severity"));
    }

    // ---- YAML round trip -----------------------------------------------------------------

    /// <summary>The fields have to survive the actual loader, not just the object model.</summary>
    [Fact]
    public void The_loader_reads_every_new_field()
    {
        const string yaml = """
        - id: full
          ecosystem: node
          ecosystems:
            - go
          category: test
          title: Full rule
          match: "boom"
          notMatch: "flake"
          requires: "Running tests"
          severity: warning
          enabled: true
          confidence: 0.7
          fix: x
          docs: https://example.com
        """;

        var rule = RulePackLoader.ParseDocument(yaml).Single();

        rule.Ecosystems.Should().Equal("go");
        rule.AllEcosystems.Should().BeEquivalentTo(new[] { "node", "go" });
        rule.NotMatch.Should().Be("flake");
        rule.Requires.Should().Be("Running tests");
        rule.Severity.Should().Be("warning");
        rule.Enabled.Should().BeTrue();
    }
}
