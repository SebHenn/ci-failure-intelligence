using System.Net.Http.Headers;
using System.Text;
using CiFail.Core.Models;
using CiFail.Core.Output;

namespace CiFail.Core.Storage;

/// <summary>
/// Posts a log to a remote <c>cifail serve</c> for analysis (R18). Unlike
/// <see cref="HttpAnalysisStore"/> (which only reads/resolves), this drives the server's
/// <c>POST /analyze</c> — the full pipeline (rules + similarity + persistence + notifications)
/// runs server-side, and the shared <see cref="AnalysisJson"/> contract is deserialized back
/// into a domain <see cref="Models.Analysis"/> so the CLI renders it identically to a local run.
/// </summary>
public sealed class HttpAnalyzeClient : IDisposable
{
    private readonly HttpClient _http;

    public HttpAnalyzeClient(string baseUrl, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("a server URL is required (e.g. http://cifail:8080)", nameof(baseUrl));
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Analyze one log on the server and return the reconstructed domain result.</summary>
    public Models.Analysis Analyze(string log, string? type, string source, bool noHistory)
    {
        var query = $"analyze?source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrWhiteSpace(type)) query += $"&type={Uri.EscapeDataString(type)}";
        if (noHistory) query += "&noHistory=1";

        var request = new HttpRequestMessage(HttpMethod.Post, query)
        {
            Content = new StringContent(log, Encoding.UTF8, "text/plain"),
        };
        var response = _http.Send(request);
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(response.Content.ReadAsStream());
        var dto = AnalysisJson.Deserialize(reader.ReadToEnd())
                  ?? throw new InvalidOperationException("the server returned an empty analysis response");
        return AnalysisJson.FromDto(dto);
    }

    public void Dispose() => _http.Dispose();
}
