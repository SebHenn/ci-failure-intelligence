using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CiFail.Core.Notifications;
using CiFail.Core.Output;
using FluentAssertions;

namespace CiFail.Server.Tests;

/// <summary>
/// Proves the server dispatches notifications: a first-seen fingerprint fires <c>NewFailure</c>,
/// the same log again fires <c>Recurrence</c>, and resolving fires <c>Resolved</c>.
/// </summary>
public sealed class ServeNotifyTests : IClassFixture<NotifyServeFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private const string MatchingLog =
        "error NU1101: Unable to find package Newtonsoft.Jsn. No packages exist with this id in source(s): nuget.org";

    private readonly NotifyServeFixture _fixture;
    private readonly HttpClient _client;

    public ServeNotifyTests(NotifyServeFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Analyze_then_reanalyze_then_resolve_fires_new_recurrence_and_resolved()
    {
        var first = await Analyze(MatchingLog);
        var second = await Analyze(MatchingLog);

        var resolve = await _client.PostAsync($"resolve/{second.HistoryId}",
            new StringContent("{\"note\":\"fixed\"}", Encoding.UTF8, "application/json"));
        resolve.EnsureSuccessStatusCode();

        var fingerprint = first.Fingerprint;
        var forThis = _fixture.Notifier.Received
            .Where(n => n.Analysis.Fingerprint == fingerprint)
            .ToList();

        forThis.Select(n => n.Event).Should().ContainInOrder(
            NotificationEvent.NewFailure,
            NotificationEvent.Recurrence,
            NotificationEvent.Resolved);
    }

    private async Task<AnalysisJson.AnalysisDto> Analyze(string log)
    {
        var response = await _client.PostAsync("analyze?source=test.log",
            new StringContent(log, Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<AnalysisJson.AnalysisDto>(
            await response.Content.ReadAsStringAsync(), Json)!;
    }
}
