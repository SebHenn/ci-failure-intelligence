namespace CiFail.Core.Storage;

/// <summary>
/// Built-in provider for the embedded SQLite store (the zero-config default). The
/// connection string, when given, is treated as the database file path; otherwise the
/// default <c>~/.cifail/history.db</c> is used.
/// </summary>
public sealed class SqliteStoreProvider : IStoreProvider
{
    public string Name => "sqlite";

    public string Description => "Embedded local SQLite file (default, no setup).";

    public IAnalysisStore Create(string? connectionString) =>
        new SqliteAnalysisRepository(string.IsNullOrWhiteSpace(connectionString) ? null : connectionString);
}
