using CiFail.Core.Configuration;
using CiFail.Server;
using Microsoft.AspNetCore.Builder;

namespace CiFail.Server.Tests;

/// <summary>
/// Boots serve with two per-client named tokens (R20) so individual rotation/revocation is
/// exercised: each token authorizes, an unknown one does not.
/// </summary>
public sealed class MultiTokenServeFixture : IAsyncLifetime
{
    public const string TokenA = "client-a-token";
    public const string TokenB = "client-b-token";

    private readonly string _home;
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public MultiTokenServeFixture()
    {
        _home = Path.Combine(Path.GetTempPath(), "cifail-multitoken-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CIFAIL_HOME", _home);
    }

    public async Task InitializeAsync()
    {
        _app = CiFailServer.Build(new ServeOptions
        {
            Url = "http://127.0.0.1:0",
            Database = new DatabaseConfig { Provider = "sqlite" },
            AuthTokens = new[]
            {
                new NamedToken("client-a", TokenA),
                new NamedToken("client-b", TokenB),
            },
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
