using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CiFail.Core.Notifications.Channels;

/// <summary>
/// <see cref="IGitHubIssueClient"/> backed by the GitHub REST API. Lists open issues carrying the
/// cifail label to find the tracking issue by marker, then creates an issue or comments. Raw
/// <see cref="HttpClient"/> (no Octokit) with a short timeout, like the other channels.
/// </summary>
public sealed class HttpGitHubIssueClient : IGitHubIssueClient
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _repo;
    private readonly string _labelQuery;

    public HttpGitHubIssueClient(string repo, string token, string? apiBaseUrl, IReadOnlyList<string> labels)
    {
        _repo = repo.Trim('/');
        var baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.github.com" : apiBaseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cifail");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _labelQuery = labels.Count > 0 ? "&labels=" + Uri.EscapeDataString(string.Join(",", labels)) : "";
    }

    public int? FindOpenIssue(string marker)
    {
        // Scan the cifail-labelled open issues for the marker in the body.
        using var response = _http.Send(new HttpRequestMessage(
            HttpMethod.Get, $"repos/{_repo}/issues?state=open&per_page=100{_labelQuery}"));
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        var issues = JsonSerializer.Deserialize<List<IssueDto>>(stream, Read) ?? new();
        return issues.FirstOrDefault(i => i.Body is not null && i.Body.Contains(marker, StringComparison.Ordinal))?.Number;
    }

    public int CreateIssue(string title, string body, IReadOnlyList<string> labels)
    {
        var payload = JsonSerializer.Serialize(new { title, body, labels });
        using var response = _http.Send(new HttpRequestMessage(HttpMethod.Post, $"repos/{_repo}/issues")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        return JsonSerializer.Deserialize<IssueDto>(stream, Read)?.Number ?? 0;
    }

    public void CommentOnIssue(int number, string body)
    {
        var payload = JsonSerializer.Serialize(new { body });
        using var response = _http.Send(new HttpRequestMessage(
            HttpMethod.Post, $"repos/{_repo}/issues/{number}/comments")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed class IssueDto
    {
        public int Number { get; init; }
        public string? Body { get; init; }
    }
}
