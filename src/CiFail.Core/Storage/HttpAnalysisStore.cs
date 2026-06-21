using System.Net;
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
/// Read + manual-resolve are implemented here against the R7 endpoints. The write-back paths
/// used by analyze (<see cref="Save"/>, <see cref="LoadCorpus"/>) and the auto-resolution
/// methods (<see cref="GetOpenFailures"/>, <see cref="SetAutoResolution"/>) are not yet served
/// remotely — the latter two land with server-side reconciliation (R11).
/// </summary>
public sealed class HttpAnalysisStore : IAnalysisStore
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public HttpAnalysisStore(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("a server URL is required (e.g. http://cifail:8080)", nameof(baseUrl));
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
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

    public long Save(AnalysisRecord record) => throw new NotSupportedException(
        "A remote cifail server analyzes and stores logs itself; the http store does not save raw records.");

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max) => throw new NotSupportedException(
        "Similarity is computed by the remote cifail server; the http store has no local corpus.");

    public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => throw new NotSupportedException(
        "Server-side reconciliation is not available yet (planned for R11).");

    public bool SetAutoResolution(long id, string resolvedCommit, string note) => throw new NotSupportedException(
        "Server-side reconciliation is not available yet (planned for R11).");

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

    public IAnalysisStore Create(string? connectionString) => new HttpAnalysisStore(connectionString!);
}
