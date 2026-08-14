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
    /// Additional per-client named tokens (R20), so clients can be rotated/revoked individually.
    /// Combined with <see cref="AuthToken"/>; any one matching token authorizes a request.
    /// </summary>
    public IReadOnlyList<NamedToken> AuthTokens { get; init; } = Array.Empty<NamedToken>();

    /// <summary>
    /// Optional path to a PEM bundle of trusted client-certificate authorities (R20). When set,
    /// the server requires mutual TLS: each client must present a cert that chains to one of these
    /// CAs. Requires <see cref="TlsCertPath"/> (mTLS needs server TLS too).
    /// </summary>
    public string? ClientCaPath { get; init; }

    /// <summary>Server TLS certificate (PFX/PKCS#12) used when mTLS is enabled.</summary>
    public string? TlsCertPath { get; init; }

    /// <summary>Password for <see cref="TlsCertPath"/>, if the PFX is encrypted.</summary>
    public string? TlsCertPassword { get; init; }

    /// <summary>All bearer tokens the server accepts: the single token (named <c>default</c>) plus the list.</summary>
    public IReadOnlyList<NamedToken> ResolvedTokens()
    {
        var tokens = new List<NamedToken>();
        if (!string.IsNullOrWhiteSpace(AuthToken))
            tokens.Add(new NamedToken("default", AuthToken));
        tokens.AddRange(AuthTokens);
        return tokens;
    }

    /// <summary>True when mutual TLS is configured (a client CA is supplied).</summary>
    public bool MutualTls => !string.IsNullOrWhiteSpace(ClientCaPath);

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

    /// <summary>
    /// Largest log <c>POST /analyze</c> will accept, in bytes (default 10 MB).
    ///
    /// <para>
    /// The body is materialized as a single string and then normalized, scrubbed and tokenized —
    /// several full-size copies — so an unbounded body is a memory-exhaustion lever for anyone who
    /// can reach the port. Kestrel's 30 MB default was the only thing standing in the way, and it
    /// answers with a bare protocol error rather than something a client can act on.
    /// </para>
    /// </summary>
    public long MaxLogBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Ceiling on <c>?limit=</c> for the list endpoints (default 1000). Without one,
    /// <c>GET /history?limit=2000000000</c> serializes the whole table into one response.
    /// </summary>
    public int MaxPageSize { get; init; } = 1000;

    public string ResolvedUrl => Url ?? $"http://{Host}:{Port}";
}
