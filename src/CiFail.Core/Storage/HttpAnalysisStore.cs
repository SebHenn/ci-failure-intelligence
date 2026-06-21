using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CiFail.Core.Output;

namespace CiFail.Core.Storage;

/// <summary>
/// An <see cref="IAnalysisStore"/> backed by a remote <c>cifail serve</c> instance over
/// HTTP — so <c>cifail history</c> / <c>cifail resolve</c> can target a shared team service
/// without the client holding any database credentials. Registered as the <c>http</c>
/// provider; the connection string is the server's base URL (e.g. <c>http://cifail:8080</c>).
///
/// Read, manual-resolve, and (R11) the auto-resolution pair (<see cref="GetOpenFailures"/> /
/// <see cref="SetAutoResolution"/>) are implemented here, so the client-side
/// <c>ResolutionReconciler</c> runs unchanged against a remote server. The analyze write paths
/// (<see cref="Save"/>, <see cref="LoadCorpus"/>) are not served remotely — a server analyzes
/// and stores logs itself via <c>POST /analyze</c>.
/// </summary>
public sealed class HttpAnalysisStore : IAnalysisStore
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Env var holding the shared bearer token (matches <c>cifail serve --token</c>).</summary>
    public const string TokenEnvVar = "CIFAIL_SERVER_TOKEN";

    private readonly HttpClient _http;

    public HttpAnalysisStore(string baseUrl, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("a server URL is required (e.g. http://cifail:8080)", nameof(baseUrl));
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public IReadOnlyList<StoredAnalysis> GetRecent(int limit)
    {
        var dtos = GetJson<List<StoredAnalysisJson.StoredAnalysisDto>>($"history?limit={limit}");
        return dtos is null
            ? Array.Empty<StoredAnalysis>()
            : dtos.Select(StoredAnalysisJson.FromDto).ToList();
    }

    public StoredAnalysis? GetById(long id)
    {
        var response = _http.Send(new HttpRequestMessage(HttpMethod.Get, $"history/{id}"));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var dto = ReadJson<StoredAnalysisJson.StoredAnalysisDto>(response);
        return dto is null ? null : StoredAnalysisJson.FromDto(dto);
    }

    public bool SetResolution(long id, string note)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"resolve/{id}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { note }), Encoding.UTF8, "application/json"),
        };
        var response = _http.Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId)
    {
        var dtos = GetJson<List<StoredAnalysisJson.StoredAnalysisDto>>(
            $"repos/{Uri.EscapeDataString(repoId)}/open");
        return dtos is null
            ? Array.Empty<StoredAnalysis>()
            : dtos.Select(StoredAnalysisJson.FromDto).ToList();
    }

    public bool SetAutoResolution(long id, string resolvedCommit, string note)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"resolve/{id}?source=auto&commit={Uri.EscapeDataString(resolvedCommit)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { note }), Encoding.UTF8, "application/json"),
        };
        var response = _http.Send(request);
        // 404 = unknown id OR already resolved (manual wins) — both mean "didn't auto-resolve".
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public long Save(AnalysisRecord record) => throw new NotSupportedException(
        "A remote cifail server analyzes and stores logs itself; the http store does not save raw records.");

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max) => throw new NotSupportedException(
        "Similarity is computed by the remote cifail server; the http store has no local corpus.");

    public void Dispose() => _http.Dispose();

    private T? GetJson<T>(string path)
    {
        var response = _http.Send(new HttpRequestMessage(HttpMethod.Get, path));
        response.EnsureSuccessStatusCode();
        return ReadJson<T>(response);
    }

    private static T? ReadJson<T>(HttpResponseMessage response)
    {
        using var stream = response.Content.ReadAsStream();
        return JsonSerializer.Deserialize<T>(stream, ReadOptions);
    }
}

/// <summary>Provider for the remote <c>http</c> store. Always available (BCL-only client).</summary>
public sealed class HttpStoreProvider : IStoreProvider
{
    public string Name => "http";

    public string Description => "Remote cifail serve instance over HTTP (use --server <url>).";

    public IAnalysisStore Create(string? connectionString) =>
        new HttpAnalysisStore(connectionString!, Environment.GetEnvironmentVariable(HttpAnalysisStore.TokenEnvVar));
}
