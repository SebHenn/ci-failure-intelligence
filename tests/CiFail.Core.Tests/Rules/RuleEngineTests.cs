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

    /// <summary>
    /// Rule packs are untrusted input: since R14 cifail loads them from the <c>.cifail/rules</c>
    /// directory of whatever repository you are working in, so a pattern with catastrophic
    /// backtracking — hostile or, far more likely, accidental — would otherwise hang the CLI and
    /// pin a request thread in <c>cifail serve</c>.
    ///
    /// <para>
    /// The pattern below is the classic nested-quantifier ReDoS: against a run of 'a' with no
    /// trailing '!', the engine explores every partition of the run. Before the timeout this test
    /// did not finish. It also proves the guard is not a green no-op, which is the trap the v7
    /// round hit with a markup check that could never fail.
    /// </para>
    /// </summary>
    [Fact]
    public void A_catastrophically_backtracking_rule_is_skipped_rather_than_hanging()
    {
        var engine = new RuleEngine(new[]
        {
            new RuleDefinition
            {
                Id = "evil", Ecosystem = "generic", Category = "test", Title = "evil",
                Match = "(a+)+!", Confidence = 0.9, Fix = "x",
            },
            new RuleDefinition
            {
                Id = "wellbehaved", Ecosystem = "generic", Category = "test", Title = "fine",
                Match = "boom", Confidence = 0.5, Fix = "x",
            },
        });
        var log = LogNormalizer.Build("t", new string('a', 40) + "\nboom");
        var diagnostics = new List<string>();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var matches = engine.Match(log, Ecosystem.Generic, diagnostics);
        elapsed.Stop();

        // Bounded by the timeout, not by the pattern finishing. Generous headroom over the 2s
        // budget so a slow CI runner can't make this flaky.
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));

        // The bad rule is reported and dropped; every other rule still ran.
        diagnostics.Should().ContainSingle().Which.Should().Contain("evil").And.Contain("skipped");
        matches.Should().ContainSingle().Which.Rule.Id.Should().Be("wellbehaved");
    }

    [Fact]
    public void An_invalid_pattern_is_reported_rather_than_silently_dropped()
    {
        var engine = new RuleEngine(new[]
        {
            new RuleDefinition
            {
                Id = "broken", Ecosystem = "generic", Category = "test", Title = "broken",
                Match = "(unclosed", Confidence = 0.9, Fix = "x",
            },
        });

        var diagnostics = new List<string>();
        engine.Match(LogNormalizer.Build("t", "anything"), Ecosystem.Generic, diagnostics)
            .Should().BeEmpty();

        diagnostics.Should().ContainSingle().Which.Should().Contain("broken");
    }

    /// <summary>
    /// The regex cache used to be keyed on the rule id, so two rules sharing an id — possible
    /// whenever a RuleEngine is built directly, since only RulePackLoader dedupes — made the
    /// second silently evaluate the first one's pattern.
    /// </summary>
    [Fact]
    public void Two_rules_sharing_an_id_each_use_their_own_pattern()
    {
        var engine = new RuleEngine(new[]
        {
            new RuleDefinition
            {
                Id = "dupe", Ecosystem = "generic", Category = "test", Title = "first",
                Match = "alpha", Confidence = 0.9, Fix = "x",
            },
            new RuleDefinition
            {
                Id = "dupe", Ecosystem = "generic", Category = "test", Title = "second",
                Match = "beta", Confidence = 0.8, Fix = "x",
            },
        });

        var matches = engine.Match(LogNormalizer.Build("t", "beta only"), Ecosystem.Generic);

        matches.Should().ContainSingle().Which.Rule.Title.Should().Be("second");
    }

    /// <summary>
    /// Android and Scala builds inherit the JVM rules, because they genuinely are JVM builds.
    /// Before this, an Android job that ran out of memory got the vague `generic-oom` while
    /// `gradle-daemon-disappeared` sat unused in java.yaml — and Kotlin rules, which live in
    /// that same pack, went unapplied on the platform where most Kotlin is written.
    /// </summary>
    [Theory]
    [InlineData(Ecosystem.Android)]
    [InlineData(Ecosystem.Scala)]
    public void A_jvm_ecosystem_inherits_the_java_rules(Ecosystem ecosystem)
    {
        var engine = new RuleEngine(new[]
        {
            new RuleDefinition
            {
                Id = "java-only", Ecosystem = "java", Title = "Java only",
                Match = "OutOfMemoryError", Confidence = 0.8, Fix = "x",
            },
        });
        var log = LogNormalizer.Build("t", "java.lang.OutOfMemoryError: Java heap space");

        engine.Match(log, ecosystem).Should().ContainSingle()
            .Which.Rule.Id.Should().Be("java-only");
    }

    [Fact]
    public void Inheritance_does_not_run_in_the_other_direction()
    {
        // Java must not pick up Android's rules: an ordinary Maven build has nothing to do
        // with aapt2 or a manifest merger, and matching those would be pure noise.
        var engine = new RuleEngine(new[]
        {
            new RuleDefinition
            {
                Id = "android-only", Ecosystem = "android", Title = "Android only",
                Match = "boom", Confidence = 0.8, Fix = "x",
            },
        });
        var log = LogNormalizer.Build("t", "boom");

        engine.Match(log, Ecosystem.Java).Should().BeEmpty();
        engine.Match(log, Ecosystem.Android).Should().ContainSingle();
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
