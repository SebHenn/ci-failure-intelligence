using CiFail.Core.Configuration;
using CiFail.Server;
using Microsoft.AspNetCore.Builder;

namespace CiFail.Server.Tests;

/// <summary>
/// Boots a real <c>cifail serve</c> instance on a random loopback port backed by a temp
/// SQLite file, and exposes an <see cref="HttpClient"/> pointed at it. No Docker needed.
/// </summary>
public sealed class ServeFixture : IAsyncLifetime
{
    private readonly string _home;
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public ServeFixture()
    {
        // Isolate history from the real ~/.cifail. Must be set before any store opens.
        _home = Path.Combine(Path.GetTempPath(), "cifail-server-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CIFAIL_HOME", _home);
    }

    public async Task InitializeAsync()
    {
        _app = CiFailServer.Build(new ServeOptions
        {
            Url = "http://127.0.0.1:0", // 0 => OS picks a free port
            Database = new DatabaseConfig { Provider = "sqlite" },
        });
        await _app.StartAsync();

        var baseUrl = _app.Urls.First();
        Client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
