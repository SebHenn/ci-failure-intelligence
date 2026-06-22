using CiFail.Core.Ai;
using CiFail.Core.Configuration;
using CiFail.Core.Notifications;

namespace CiFail.Server;

/// <summary>Settings for a <c>cifail serve</c> instance.</summary>
public sealed class ServeOptions
{
    /// <summary>Interface to bind. Defaults to all interfaces (container-friendly).</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>TCP port to listen on.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Full bind URL; overrides <see cref="Host"/>/<see cref="Port"/> when set
    /// (e.g. <c>http://127.0.0.1:0</c> for tests, where 0 picks a free port).</summary>
    public string? Url { get; init; }

    /// <summary>Which database the service persists to (resolved from CLI/env/config).</summary>
    public DatabaseConfig Database { get; init; } = new();

    /// <summary>
    /// Shared bearer token required on every request except <c>/healthz</c>. When null/empty
    /// the server runs open (and logs a loud warning) — fine for a trusted network or dev,
    /// never for an exposed one.
    /// </summary>
    public string? AuthToken { get; init; }

    /// <summary>
    /// Optional embedder (R10): when set, each analyzed log is embedded and, if the store is
    /// vector-capable (pgvector), similarity is served from the database. Null = TF-IDF as before.
    /// </summary>
    public IAiEmbedder? Embedder { get; init; }

    /// <summary>
    /// Optional outbound notifications (R13): when set, the server fires new-failure / recurrence
    /// on <c>/analyze</c> and resolved on <c>/resolve</c>. Null = no notifications.
    /// </summary>
    public NotificationDispatcher? Notifications { get; init; }

    public string ResolvedUrl => Url ?? $"http://{Host}:{Port}";
}
