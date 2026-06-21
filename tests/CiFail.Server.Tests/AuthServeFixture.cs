using CiFail.Core.Configuration;
using CiFail.Server;
using Microsoft.AspNetCore.Builder;

namespace CiFail.Server.Tests;

/// <summary>
/// Like <see cref="ServeFixture"/> but boots the server with a required bearer token, so the
/// auth middleware (R9) is exercised end-to-end. Exposes the base URL + token; tests attach the
/// header themselves (the client here carries none by default).
/// </summary>
public sealed class AuthServeFixture : IAsyncLifetime
{
    public const string Token = "s3cr3t-test-token";

    private readonly string _home;
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public AuthServeFixture()
    {
        _home = Path.Combine(Path.GetTempPath(), "cifail-auth-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CIFAIL_HOME", _home);
    }

    public async Task InitializeAsync()
    {
        _app = CiFailServer.Build(new ServeOptions
        {
            Url = "http://127.0.0.1:0",
            Database = new DatabaseConfig { Provider = "sqlite" },
            AuthToken = Token,
        });
        await _app.StartAsync();

        BaseUrl = _app.Urls.First();
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/") };
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
