using CiFail.Core.Ai;
using CiFail.Core.Ai.Providers;
using CiFail.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Ai;

/// <summary>
/// Real round-trip against a local Ollama embedding model. Gated on <c>CIFAIL_AI_IT=1</c> (like
/// the analyzer integration test) so it's skipped by default. Pull the model first, e.g.
/// <c>ollama pull nomic-embed-text</c>. Override with <c>CIFAIL_AI_EMBED_MODEL</c>.
/// </summary>
public class OllamaEmbedderTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CIFAIL_AI_IT") == "1";

    [Fact]
    public void Ollama_embeds_text_into_a_vector()
    {
        if (!Enabled) return; // skipped unless CIFAIL_AI_IT=1

        var config = new AiConfig
        {
            Provider = "ollama",
            EmbeddingModel = Environment.GetEnvironmentVariable("CIFAIL_AI_EMBED_MODEL")
                ?? OllamaProvider.DefaultEmbeddingModel,
        };
        var embedder = AiFactory.CreateEmbedder(config);
        embedder.Should().NotBeNull();

        var vector = embedder!.Embed("error NU1101: unable to find package Foo");

        vector.Should().NotBeNull();
        vector!.Length.Should().BeGreaterThan(0);
        // nomic-embed-text emits 768 dimensions by default.
        vector.Should().Contain(v => v != 0f);
    }
}
