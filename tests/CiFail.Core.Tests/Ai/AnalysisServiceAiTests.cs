using CiFail.Core.Analysis;
using CiFail.Core.Models;
using CiFail.Core.Rules;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ai;

/// <summary>
/// Proves how the optional AI step is gated in <see cref="AnalysisService"/>: only when
/// <c>--ai</c> is set AND the rules are unsure (no match or low confidence), and always
/// best-effort (a throwing analyzer never breaks the rules-only result).
/// </summary>
public class AnalysisServiceAiTests
{
    private static AnalysisService ServiceWith(FakeAiAnalyzer ai) =>
        new(new RuleEngine(RulePackLoader.LoadAll()), store: null, git: null, ai: ai);

    private static AnalysisOptions WithAi(bool enable) => new() { EnableAi = enable };

    [Fact]
    public void Ai_is_not_consulted_when_disabled_even_on_an_unmatched_log()
    {
        var ai = new FakeAiAnalyzer();
        var analysis = ServiceWith(ai).Analyze("unknown", Fixtures.Load("unknown.log"), WithAi(false));

        ai.Calls.Should().Be(0);
        analysis.AiSuggestion.Should().BeNull();
    }

    [Fact]
    public void Ai_is_consulted_when_enabled_and_no_rule_matched()
    {
        var ai = new FakeAiAnalyzer();
        var analysis = ServiceWith(ai).Analyze("unknown", Fixtures.Load("unknown.log"), WithAi(true));

        analysis.HasMatch.Should().BeFalse();
        ai.Calls.Should().Be(1);
        analysis.AiSuggestion.Should().NotBeNull();
        analysis.AiSuggestion!.RootCause.Should().Be("fake root cause");
        analysis.AiSuggestion.Fix.Should().Be("fake fix");
    }

    [Fact]
    public void Ai_is_skipped_when_a_confident_rule_already_matched()
    {
        var ai = new FakeAiAnalyzer();
        var analysis = ServiceWith(ai).Analyze("nu1101", Fixtures.Load("nuget-nu1101.log"), WithAi(true));

        analysis.HasMatch.Should().BeTrue();
        analysis.RootCause!.Score.Should().BeGreaterThanOrEqualTo(0.6); // a high/medium-confidence hit
        ai.Calls.Should().Be(0);
        analysis.AiSuggestion.Should().BeNull();
    }

    [Fact]
    public void Ai_failure_is_swallowed_and_never_fatal()
    {
        var ai = new FakeAiAnalyzer(@throw: true);

        var act = () => ServiceWith(ai).Analyze("unknown", Fixtures.Load("unknown.log"), WithAi(true));

        var analysis = act.Should().NotThrow().Subject;
        ai.Calls.Should().Be(1);           // it was asked
        analysis.AiSuggestion.Should().BeNull(); // ...but the throw left no suggestion
    }

    [Fact]
    public void Ai_request_carries_the_detected_ecosystem_and_a_log_excerpt()
    {
        var ai = new FakeAiAnalyzer();
        ServiceWith(ai).Analyze("unknown", Fixtures.Load("unknown.log"), WithAi(true));

        ai.LastRequest.Should().NotBeNull();
        ai.LastRequest!.LogExcerpt.Should().NotBeNullOrWhiteSpace();
    }
}
