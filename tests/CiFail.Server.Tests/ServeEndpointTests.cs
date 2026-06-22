using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CiFail.Core.Analysis;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using FluentAssertions;

namespace CiFail.Server.Tests;

public sealed class ServeEndpointTests : IClassFixture<ServeFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // A log line the embedded nuget rule pack recognizes with high confidence.
    private const string MatchingLog =
        "error NU1101: Unable to find package Newtonsoft.Jsn. No packages exist with this id in source(s): nuget.org";

    private readonly HttpClient _client;

    public ServeEndpointTests(ServeFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var response = await _client.GetAsync("healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task Analyze_matches_persists_and_is_retrievable()
    {
        var analysis = await Analyze(MatchingLog);

        analysis.Matched.Should().BeTrue();
        analysis.RootCause.Should().NotBeNull();
        analysis.HistoryId.Should().NotBeNull();

        var byId = await GetStored($"history/{analysis.HistoryId}");
        byId.Should().NotBeNull();
        byId!.Fingerprint.Should().Be(analysis.Fingerprint);
        byId.Status.Should().Be("open");
    }

    [Fact]
    public async Task History_lists_the_saved_record()
    {
        var analysis = await Analyze(MatchingLog);

        var response = await _client.GetAsync("history?limit=50");
        response.EnsureSuccessStatusCode();
        var list = JsonSerializer.Deserialize<List<StoredAnalysisJson.StoredAnalysisDto>>(
            await response.Content.ReadAsStringAsync(), Json)!;

        list.Should().Contain(r => r.Id == analysis.HistoryId);
    }

    [Fact]
    public async Task Resolve_marks_record_resolved_as_manual()
    {
        var analysis = await Analyze(MatchingLog);

        var resolve = await _client.PostAsync($"resolve/{analysis.HistoryId}",
            new StringContent("{\"note\":\"fixed the typo\"}", Encoding.UTF8, "application/json"));
        resolve.EnsureSuccessStatusCode();

        var updated = JsonSerializer.Deserialize<StoredAnalysisJson.StoredAnalysisDto>(
            await resolve.Content.ReadAsStringAsync(), Json)!;
        updated.Status.Should().Be("resolved");
        updated.ResolutionSource.Should().Be("manual");
        updated.Resolution.Should().Be("fixed the typo");
    }

    [Fact]
    public async Task Stats_endpoint_reports_aggregates()
    {
        var analysis = await Analyze(MatchingLog);

        var response = await _client.GetAsync("stats?top=20");
        response.EnsureSuccessStatusCode();
        var stats = JsonSerializer.Deserialize<StatsJson.StatsDto>(
            await response.Content.ReadAsStringAsync(), Json)!;

        stats.Total.Should().BeGreaterThan(0);
        (stats.Open + stats.Resolved).Should().Be(stats.Total);
        stats.TopFailures.Should().Contain(f => f.Fingerprint == analysis.Fingerprint);
        stats.ByEcosystem.Should().Contain(c => c.Key == "dotnet");
    }

    [Fact]
    public async Task HttpStore_ComputeStats_round_trips_from_server()
    {
        // `cifail stats --server` path: the http store delegates to GET /stats and rebuilds
        // the snapshot from the shared JSON contract.
        var analysis = await Analyze(MatchingLog);

        using var store = new HttpAnalysisStore(_client.BaseAddress!.ToString());
        var stats = store.ComputeStats(new StatsQuery { Top = 20 });

        stats.Total.Should().BeGreaterThan(0);
        stats.TopFailures.Should().Contain(f => f.Fingerprint == analysis.Fingerprint);
    }

    [Fact]
    public void HttpAnalyzeClient_remote_analyze_matches_local()
    {
        // R18: `cifail analyze --server` posts to /analyze and reconstructs the domain result.
        using var client = new HttpAnalyzeClient(_client.BaseAddress!.ToString());
        var remote = client.Analyze(MatchingLog, type: null, source: "test.log", noHistory: true);

        var local = AnalysisService.CreateDefault().Analyze("test.log", MatchingLog);

        remote.HasMatch.Should().Be(local.HasMatch);
        remote.RootCause!.Rule.Id.Should().Be(local.RootCause!.Rule.Id);
        remote.Fingerprint.ToString().Should().Be(local.Fingerprint.ToString());
        remote.Ecosystem.Should().Be(local.Ecosystem);
    }

    [Fact]
    public async Task Analysis_json_round_trips_through_FromDto()
    {
        // Byte-identical contract: re-serializing the reconstructed analysis equals the server body.
        var response = await _client.PostAsync("analyze?source=test.log&noHistory=1",
            new StringContent(MatchingLog, Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
        var serverJson = await response.Content.ReadAsStringAsync();

        var reconstructed = AnalysisJson.FromDto(AnalysisJson.Deserialize(serverJson)!);
        AnalysisJson.Serialize(reconstructed).Should().Be(serverJson);
    }

    [Fact]
    public async Task Unknown_history_id_returns_404()
    {
        var response = await _client.GetAsync("history/999999");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolving_unknown_id_returns_404()
    {
        var response = await _client.PostAsync("resolve/999999",
            new StringContent("{\"note\":\"x\"}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_analyze_body_returns_400()
    {
        var response = await _client.PostAsync("analyze",
            new StringContent("", Encoding.UTF8, "text/plain"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<AnalysisJson.AnalysisDto> Analyze(string log)
    {
        var response = await _client.PostAsync("analyze?source=test.log",
            new StringContent(log, Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<AnalysisJson.AnalysisDto>(
            await response.Content.ReadAsStringAsync(), Json)!;
    }

    private async Task<StoredAnalysisJson.StoredAnalysisDto?> GetStored(string path)
    {
        var response = await _client.GetAsync(path);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<StoredAnalysisJson.StoredAnalysisDto>(
            await response.Content.ReadAsStringAsync(), Json);
    }
}
