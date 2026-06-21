using System.Collections.Concurrent;

namespace CiFail.Core.Storage;

/// <summary>
/// Holds the available <see cref="IStoreProvider"/>s by name. SQLite is registered by
/// default (built into the core, no extra dependencies). External providers register
/// themselves at startup when their assembly is present.
/// </summary>
public static class StoreRegistry
{
    private static readonly ConcurrentDictionary<string, IStoreProvider> Providers = new(StringComparer.OrdinalIgnoreCase);

    static StoreRegistry()
    {
        // Both are dependency-free (SQLite ships with Core; the http client is BCL-only),
        // so they're always available even in the slim build. External engines register
        // themselves via ExternalProviders.RegisterAll() in the full build.
        Register(new SqliteStoreProvider());
        Register(new HttpStoreProvider());
    }

    public static void Register(IStoreProvider provider) => Providers[provider.Name] = provider;

    public static IStoreProvider? Get(string name) =>
        Providers.TryGetValue(name, out var p) ? p : null;

    public static IReadOnlyCollection<string> AvailableNames =>
        Providers.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
}

/// <summary>Thrown when the configured provider isn't compiled into this build.</summary>
public sealed class StoreProviderNotAvailableException : Exception
{
    public StoreProviderNotAvailableException(string requested, IReadOnlyCollection<string> available)
        : base($"Database provider '{requested}' is not available in this build of cifail. " +
               $"Available here: {string.Join(", ", available)}. " +
               "External databases (postgres, mysql, sqlserver, mongodb) ship in the cifail " +
               "Docker image and full build — see the README.")
    {
        Requested = requested;
        Available = available;
    }

    public string Requested { get; }
    public IReadOnlyCollection<string> Available { get; }
}
