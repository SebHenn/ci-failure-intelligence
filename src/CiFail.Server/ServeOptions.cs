using CiFail.Core.Configuration;

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

    public string ResolvedUrl => Url ?? $"http://{Host}:{Port}";
}
