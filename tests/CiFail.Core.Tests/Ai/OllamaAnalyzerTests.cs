using CiFail.Core.Ai;
using CiFail.Core.Ai.Providers;
using CiFail.Core.Configuration;
using CiFail.Core.Models;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ai;

/// <summary>
/// Real round-trip against a local Ollama. Gated on <c>CIFAIL_AI_IT=1</c> (mirroring the
/// <c>CIFAIL_DB_IT</c> DB integration gate) so it's skipped by default and only runs where an
/// Ollama daemon is available. Override the model with <c>CIFAIL_AI_MODEL</c>.
/// </summary>
public class OllamaAnalyzerTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("CIFAIL_AI_IT") == "1";

    [Fact]
    public void Ollama_returns_a_suggestion_for_an_unmatched_failure()
    {
        if (!Enabled) return; // skipped unless CIFAIL_AI_IT=1

        var config = new AiConfig
        {
            Provider = "ollama",
            Model = Environment.GetEnvironmentVariable("CIFAIL_AI_MODEL") ?? OllamaProvider.DefaultModel,
        };
        var analyzer = AiFactory.Create(config);

        var suggestion = analyzer.Suggest(new AiRequest
        {
            LogExcerpt = "FATAL: linker command failed with exit code 1 (use -v to see invocation)",
            Ecosystem = Ecosystem.Generic,
        });

        suggestion.Should().NotBeNull();
        suggestion!.Model.Should().StartWith("ollama/");
        (suggestion.RootCause + suggestion.Fix).Should().NotBeNullOrWhiteSpace();
    }
}
