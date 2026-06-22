using System.Collections.Concurrent;
using CiFail.Core.Configuration;
using CiFail.Core.Notifications;
using CiFail.Server;
using Microsoft.AspNetCore.Builder;

namespace CiFail.Server.Tests;

/// <summary>
/// Like <see cref="ServeFixture"/>, but wires a fake in-process notifier so tests can assert which
/// notification events the server fires on <c>/analyze</c> and <c>/resolve</c>. No Docker, no network.
/// </summary>
public sealed class NotifyServeFixture : IAsyncLifetime
{
    private readonly string _home;
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;
    public RecordingNotifier Notifier { get; } = new();

    public NotifyServeFixture()
    {
        _home = Path.Combine(Path.GetTempPath(), "cifail-notify-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CIFAIL_HOME", _home);
    }

    public async Task InitializeAsync()
    {
        // Dedupe off so a second identical analyze still fires (we assert NewFailure → Recurrence).
        var dispatcher = new NotificationDispatcher(new[] { Notifier }, events: null, dedupeWindow: TimeSpan.Zero);
        _app = CiFailServer.Build(new ServeOptions
        {
            Url = "http://127.0.0.1:0",
            Database = new DatabaseConfig { Provider = "sqlite" },
            Notifications = dispatcher,
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

/// <summary>An <see cref="INotifier"/> that just records what it was sent, for assertions.</summary>
public sealed class RecordingNotifier : INotifier
{
    private readonly ConcurrentQueue<Notification> _received = new();
    public string Name => "recording";
    public void Notify(Notification notification) => _received.Enqueue(notification);
    public IReadOnlyList<Notification> Received => _received.ToList();
}
