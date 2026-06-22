namespace CiFail.Core.Configuration;

/// <summary>Top-level cifail configuration (loaded from <c>~/.cifail/config.yaml</c>).</summary>
public sealed class CiFailConfig
{
    public DatabaseConfig Database { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public NotificationsConfig Notifications { get; set; } = new();
}

/// <summary>Outbound notifications (R13), fired server-side. All channels are opt-in.</summary>
public sealed class NotificationsConfig
{
    /// <summary>Which events to send (kebab-case: new-failure, recurrence, resolved). Empty = all.</summary>
    public List<string> Events { get; set; } = new();

    /// <summary>Slack incoming-webhook URL; null/empty disables the Slack channel.</summary>
    public string? SlackWebhookUrl { get; set; }

    /// <summary>Generic webhook URL (receives a JSON payload); null/empty disables it.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Suppress repeat sends of the same (fingerprint, event) within this many seconds.</summary>
    public int DedupeSeconds { get; set; } = 300;

    /// <summary>Optional SMTP email channel; null disables it.</summary>
    public SmtpConfig? Smtp { get; set; }
}

/// <summary>SMTP settings for the optional email notifier. The password comes from an env var.</summary>
public sealed class SmtpConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }

    /// <summary>Env var holding the SMTP password (not the password itself).</summary>
    public string PasswordEnv { get; set; } = "CIFAIL_SMTP_PASSWORD";

    public string From { get; set; } = "";
    public string To { get; set; } = "";
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

    /// <summary>Guardrails around hosted-AI calls (R20). All off (unlimited) by default.</summary>
    public AiLimitsConfig Limits { get; set; } = new();
}

/// <summary>
/// Optional budget/rate-limits for the AI analyzer (R20), so a misfiring <c>--ai</c> or a busy
/// <c>serve</c> can't run up cost. Every limit is opt-in; <c>0</c> means unlimited, which is the
/// default — so this changes nothing unless an operator sets a value.
/// </summary>
public sealed class AiLimitsConfig
{
    /// <summary>Max AI calls in one process run (0 = unlimited).</summary>
    public int MaxCallsPerRun { get; set; }

    /// <summary>Max AI calls within any rolling 60-second window (0 = unlimited).</summary>
    public int MaxCallsPerMinute { get; set; }

    /// <summary>Cap on the prompt/log-excerpt length sent to the model, in chars (0 = no cap).</summary>
    public int MaxRequestChars { get; set; }

    /// <summary>True when at least one limit is active.</summary>
    public bool Any => MaxCallsPerRun > 0 || MaxCallsPerMinute > 0 || MaxRequestChars > 0;
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
