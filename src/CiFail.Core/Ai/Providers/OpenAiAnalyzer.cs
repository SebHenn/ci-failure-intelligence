using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CiFail.Core.Configuration;
using CiFail.Core.Models;

namespace CiFail.Core.Ai.Providers;

/// <summary>
/// Hosted OpenAI analyzer via the Chat Completions API. Opt-in: makes outbound calls and needs
/// an API key (from the env var named by <see cref="AiConfig.ApiKeyEnv"/>).
/// </summary>
public sealed class OpenAiAnalyzer : IAiAnalyzer
{
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiAnalyzer(string baseUrl, string model, string apiKey)
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
            messages = new[] { new { role = "user", content = AiPrompt.Build(request) } },
            response_format = new { type = "json_object" },
        };
        var http = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = client.Send(http);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(response.Content.ReadAsStream());

        // { "choices": [ { "message": { "content": "..." } } ] }
        string? text = null;
        if (doc.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var c))
            text = c.GetString();

        return AiPrompt.Parse($"openai/{_model}", text);
    }
}

public sealed class OpenAiProvider : IAiProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com";
    public const string DefaultModel = "gpt-4o-mini";

    public string Name => "openai";

    public string Description => "Hosted OpenAI (opt-in; needs an API key, makes outbound calls).";

    public IAiAnalyzer Create(AiConfig config)
    {
        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"The openai AI provider needs an API key in the {config.ApiKeyEnv} environment variable.");
        return new OpenAiAnalyzer(config.BaseUrl ?? DefaultBaseUrl, config.Model ?? DefaultModel, key);
    }
}
