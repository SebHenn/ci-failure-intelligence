namespace CiFail.Core.Storage;

/// <summary>
/// A factory for one storage backend (e.g. sqlite, postgres). Providers register
/// themselves in the <see cref="StoreRegistry"/>; external/heavy providers live in a
/// separate assembly that is only compiled into builds that need them, so the default
/// binary stays small.
/// </summary>
public interface IStoreProvider
{
    /// <summary>Lowercase provider key, e.g. "sqlite", "postgres", "mysql".</summary>
    string Name { get; }

    /// <summary>One-line human description (shown when a provider isn't available).</summary>
    string Description { get; }

    /// <summary>
    /// Open a store for this backend. A null/empty connection string means "use the
    /// provider's sensible default" (only SQLite has one).
    /// </summary>
    IAnalysisStore Create(string? connectionString);
}
