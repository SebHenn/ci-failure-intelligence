namespace CiFail.Core.Configuration;

/// <summary>Top-level cifail configuration (loaded from <c>~/.cifail/config.yaml</c>).</summary>
public sealed class CiFailConfig
{
    public DatabaseConfig Database { get; set; } = new();
}

/// <summary>Which storage backend to use and how to connect to it.</summary>
public sealed class DatabaseConfig
{
    /// <summary>Provider key: sqlite (default), postgres, mysql, sqlserver, mongodb.</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>
    /// Native connection string for the chosen provider. Null/empty means "use the
    /// provider's default" — for sqlite that's the local <c>~/.cifail/history.db</c>.
    /// </summary>
    public string? ConnectionString { get; set; }
}
