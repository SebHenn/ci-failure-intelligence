using CiFail.Core.Configuration;

namespace CiFail.Core.Storage;

/// <summary>Resolves a <see cref="DatabaseConfig"/> into a concrete <see cref="IAnalysisStore"/>.</summary>
public static class StoreFactory
{
    public static IAnalysisStore Create(DatabaseConfig database)
    {
        var provider = StoreRegistry.Get(database.Provider)
            ?? throw new StoreProviderNotAvailableException(database.Provider, StoreRegistry.AvailableNames);

        return provider.Create(database.ConnectionString);
    }

    /// <summary>Convenience overload that loads config (file + env + CLI) and builds the store.</summary>
    public static IAnalysisStore Create(string? cliProvider = null, string? cliConnection = null) =>
        Create(ConfigLoader.Load(cliProvider, cliConnection).Database);
}
