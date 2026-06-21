namespace CiFail.Core.Configuration;

/// <summary>Top-level cifail configuration (loaded from <c>~/.cifail/config.yaml</c>).</summary>
public sealed class CiFailConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
}

/// <summary>Which optional AI analyzer to consult on low rule confidence (CLI: --ai).</summary>
public sealed class AiConfig
{
    /// <summary>Provider key: ollama (default, local), anthropic, openai.</summary>
    public string Provider { get; set; } = "ollama";

    /// <summary>Model name; null means the provider's sensible default.</summary>
    public string? Model { get; set; }

    /// <summary>API base URL override; null means the provider's default endpoint.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Env var holding the API key for hosted providers (not the key itself).</summary>
    public string ApiKeyEnv { get; set; } = "CIFAIL_AI_KEY";

    /// <summary>
    /// Opt-in (R10): compute a dense embedding per analysis so a vector-capable store
    /// (e.g. pgvector) can do nearest-neighbour similarity. Off by default — the offline
    /// default uses in-app TF-IDF and needs no model.
    /// </summary>
    public bool Embeddings { get; set; }

    /// <summary>Embedding model name; null means the provider's embedding default.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// Output dimensionality of the embeddings — must match the vector column the store creates
    /// (the pgvector provider reads the same value). Default suits the Ollama
    /// <c>nomic-embed-text</c> default; hosted providers that support it are asked to emit this size.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 768;
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
