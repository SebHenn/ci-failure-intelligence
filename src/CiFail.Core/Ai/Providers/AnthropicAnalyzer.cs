using System.Text;
using System.Text.Json;
using CiFail.Core.Configuration;
using CiFail.Core.Models;

namespace CiFail.Core.Ai.Providers;

/// <summary>
/// Hosted Anthropic (Claude) analyzer via the Messages API. Opt-in: makes outbound calls and
/// needs an API key (from the env var named by <see cref="AiConfig.ApiKeyEnv"/>).
/// </summary>
public sealed class AnthropicAnalyzer : IAiAnalyzer
{
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;

    public AnthropicAnalyzer(string baseUrl, string model, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
    }

    public AiSuggestion? Suggest(AiRequest request)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var payload = new
        {
            model = _model,
            max_tokens = 512,
            messages = new[] { new { role = "user", content = AiPrompt.Build(request) } },
        };
        var http = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        http.Headers.Add("x-api-key", _apiKey);
        http.Headers.Add("anthropic-version", "2023-06-01");

        using var response = client.Send(http);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(response.Content.ReadAsStream());

        // { "content": [ { "type": "text", "text": "..." } ] }
        string? text = null;
        if (doc.RootElement.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
            && content[0].TryGetProperty("text", out var t))
            text = t.GetString();

        return AiPrompt.Parse($"anthropic/{_model}", text);
    }
}

public sealed class AnthropicProvider : IAiProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string DefaultModel = "claude-haiku-4-5-20251001";

    public string Name => "anthropic";

    public string Description => "Hosted Anthropic Claude (opt-in; needs an API key, makes outbound calls).";

    public IAiAnalyzer Create(AiConfig config)
    {
        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"The anthropic AI provider needs an API key in the {config.ApiKeyEnv} environment variable.");
        return new AnthropicAnalyzer(config.BaseUrl ?? DefaultBaseUrl, config.Model ?? DefaultModel, key);
    }

    // Anthropic has no first-party embeddings API (they recommend a dedicated embeddings
    // provider), so embedding-backed similarity isn't available with this provider.
    public IAiEmbedder? CreateEmbedder(AiConfig config) => null;
}
