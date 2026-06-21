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
}
