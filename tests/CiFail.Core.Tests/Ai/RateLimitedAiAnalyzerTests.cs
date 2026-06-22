using CiFail.Core.Ai;
using CiFail.Core.Configuration;
using CiFail.Core.Models;
using FluentAssertions;

namespace CiFail.Core.Tests.Ai;

/// <summary>The R20 cost guardrails: budgets cap how often the underlying analyzer is called.</summary>
public sealed class RateLimitedAiAnalyzerTests
{
    private static AiRequest Request(string excerpt = "boom") =>
        new() { LogExcerpt = excerpt, Ecosystem = Ecosystem.Generic };

    [Fact]
    public void Wrap_returns_inner_unchanged_when_no_limits_set()
    {
        var inner = new FakeAiAnalyzer();
        RateLimitedAiAnalyzer.Wrap(inner, new AiLimitsConfig()).Should().BeSameAs(inner);
    }

    [Fact]
    public void Caps_total_calls_per_run()
    {
        var inner = new FakeAiAnalyzer();
        var limited = new RateLimitedAiAnalyzer(inner, new AiLimitsConfig { MaxCallsPerRun = 2 });

        limited.Suggest(Request()).Should().NotBeNull();
        limited.Suggest(Request()).Should().NotBeNull();
        limited.Suggest(Request()).Should().BeNull(); // budget spent — skipped, not an error

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public void Caps_calls_per_minute_using_the_injected_clock()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var inner = new FakeAiAnalyzer();
        var limited = new RateLimitedAiAnalyzer(inner, new AiLimitsConfig { MaxCallsPerMinute = 1 }, () => now);

        limited.Suggest(Request()).Should().NotBeNull();
        limited.Suggest(Request()).Should().BeNull(); // same minute, over budget

        now = now.AddSeconds(61); // window rolls past the first call
        limited.Suggest(Request()).Should().NotBeNull();

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public void Trims_the_excerpt_to_the_char_cap()
    {
        var inner = new FakeAiAnalyzer();
        var limited = new RateLimitedAiAnalyzer(inner, new AiLimitsConfig { MaxRequestChars = 5 });

        limited.Suggest(Request("0123456789"));

        inner.LastRequest!.LogExcerpt.Should().Be("01234");
    }
}
