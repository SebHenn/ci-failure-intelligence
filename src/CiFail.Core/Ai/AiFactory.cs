using CiFail.Core.Configuration;

namespace CiFail.Core.Ai;

/// <summary>Resolves an <see cref="AiConfig"/> to a concrete <see cref="IAiAnalyzer"/>.</summary>
public static class AiFactory
{
    public static IAiAnalyzer Create(AiConfig config)
    {
        var provider = AiRegistry.Get(config.Provider)
            ?? throw new AiProviderNotAvailableException(config.Provider, AiRegistry.AvailableNames);
        // Apply the optional cost guardrails (R20); a no-op unless a limit is configured.
        return RateLimitedAiAnalyzer.Wrap(provider.Create(config), config.Limits);
    }

    /// <summary>
    /// Resolve an <see cref="IAiRuleDrafter"/> for the configured provider (R23), or null if that
    /// provider can't draft rules. Returns the raw provider analyzer (not the rate-limit wrapper):
    /// drafting is a single, explicit, interactive call, not the automated suggestion loop the
    /// budget guards target. Throws <see cref="AiProviderNotAvailableException"/> for an unknown provider.
    /// </summary>
    public static IAiRuleDrafter? CreateDrafter(AiConfig config)
    {
        var provider = AiRegistry.Get(config.Provider)
            ?? throw new AiProviderNotAvailableException(config.Provider, AiRegistry.AvailableNames);
        return provider.Create(config) as IAiRuleDrafter;
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
