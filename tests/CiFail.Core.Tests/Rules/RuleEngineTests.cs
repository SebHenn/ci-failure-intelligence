using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Rules;

public class RuleEngineTests
{
    private static RuleEngine Engine() => new(RulePackLoader.LoadEmbedded());

    private static RuleMatch? Top(string fixtureName)
    {
        var log = LogNormalizer.Build(fixtureName, Fixtures.Load(fixtureName));
        var ecosystem = EcosystemDetector.Detect(log);
        return Engine().Match(log, ecosystem) is { Count: > 0 } m ? m[0] : null;
    }

    [Fact]
    public void Matches_nu1101_with_package_capture()
    {
        var match = Top("nuget-nu1101.log");

        match.Should().NotBeNull();
        match!.Rule.Id.Should().Be("nuget-nu1101");
        match.Rule.Category.Should().Be("dependency");
        match.Score.Should().BeApproximately(0.9, 0.001);
        match.Captures.Should().ContainKey("package");
        match.Captures["package"].Should().Be("Newtonsoft.Jsn");
        match.Fix.Should().Contain("Newtonsoft.Jsn");
    }

    [Fact]
    public void Matches_nu1605_downgrade_with_package_capture()
    {
        var match = Top("nuget-nu1605-downgrade.log");

        match.Should().NotBeNull();
        match!.Rule.Id.Should().Be("nuget-nu1605");
        match.Captures["package"].Should().Be("System.Text.Json");
    }

    [Fact]
    public void Unknown_log_produces_no_match()
    {
        Top("unknown.log").Should().BeNull();
    }

    [Fact]
    public void Match_starting_on_a_newline_does_not_crash()
    {
        // A pattern whose first token is \s can match the preceding newline, so the match index
        // lands on '\n'. MatchedLine must not produce a negative-length slice (regression).
        var engine = new RuleEngine(new[]
        {
            new RuleDefinition
            {
                Id = "nl-test", Ecosystem = "generic", Category = "test", Title = "nl",
                Match = @"\s+boom", Confidence = 0.9, Fix = "x",
            },
        });
        var log = LogNormalizer.Build("t", "line one\nboom happened here");

        var act = () => engine.Match(log, Ecosystem.Generic);

        act.Should().NotThrow();
        var matches = engine.Match(log, Ecosystem.Generic);
        matches.Should().ContainSingle();
        matches[0].MatchedLine.Should().Be("boom happened here");
    }

    [Fact]
    public void Embedded_rules_have_unique_ids_and_valid_fields()
    {
        var rules = RulePackLoader.LoadEmbedded();

        rules.Should().NotBeEmpty();
        rules.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        rules.Should().OnlyContain(r =>
            !string.IsNullOrWhiteSpace(r.Match) &&
            r.Confidence > 0 && r.Confidence <= 1);
    }
}
