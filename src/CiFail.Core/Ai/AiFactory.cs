using CiFail.Core.Configuration;

namespace CiFail.Core.Ai;

/// <summary>Resolves an <see cref="AiConfig"/> to a concrete <see cref="IAiAnalyzer"/>.</summary>
public static class AiFactory
{
    public static IAiAnalyzer Create(AiConfig config)
    {
        var provider = AiRegistry.Get(config.Provider)
            ?? throw new AiProviderNotAvailableException(config.Provider, AiRegistry.AvailableNames);
        return provider.Create(config);
    }

    /// <summary>
    /// Resolve an <see cref="IAiEmbedder"/> for the configured provider, or null if that provider
    /// has no embeddings API. Throws <see cref="AiProviderNotAvailableException"/> for an unknown
    /// provider name.
    /// </summary>
    public static IAiEmbedder? CreateEmbedder(AiConfig config)
    {
        var provider = AiRegistry.Get(config.Provider)
            ?? throw new AiProviderNotAvailableException(config.Provider, AiRegistry.AvailableNames);
        return provider.CreateEmbedder(config);
    }
}
