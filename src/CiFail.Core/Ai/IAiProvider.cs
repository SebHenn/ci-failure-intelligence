using CiFail.Core.Configuration;

namespace CiFail.Core.Ai;

/// <summary>
/// A named factory for an <see cref="IAiAnalyzer"/>, mirroring the storage
/// <c>IStoreProvider</c> pattern. Providers register themselves in <see cref="AiRegistry"/>.
/// </summary>
public interface IAiProvider
{
    /// <summary>Lowercase provider key, e.g. "ollama", "anthropic", "openai".</summary>
    string Name { get; }

    /// <summary>One-line human description.</summary>
    string Description { get; }

    /// <summary>Build an analyzer from the resolved AI config (may throw if misconfigured,
    /// e.g. a hosted provider missing its API key).</summary>
    IAiAnalyzer Create(AiConfig config);

    /// <summary>
    /// Build a text embedder for similarity search (R10), or null when this provider has no
    /// embeddings API. May throw if misconfigured (e.g. a hosted provider missing its key).
    /// </summary>
    IAiEmbedder? CreateEmbedder(AiConfig config);
}
