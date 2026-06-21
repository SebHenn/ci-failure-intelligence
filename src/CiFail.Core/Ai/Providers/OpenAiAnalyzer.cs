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

/// <summary>
/// Hosted OpenAI embedder (R10) via the Embeddings API. Asks the model to emit
/// <see cref="Dimensions"/>-wide vectors (text-embedding-3-* support output reduction) so they
/// match the store's vector column.
/// </summary>
public sealed class OpenAiEmbedder : IAiEmbedder
{
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiEmbedder(string baseUrl, string model, string apiKey, int dimensions)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        Dimensions = dimensions;
    }

    public int Dimensions { get; }

    public float[]? Embed(string text)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var payload = new { model = _model, input = text, dimensions = Dimensions };
        var http = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = client.Send(http);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(response.Content.ReadAsStream());

        // { "data": [ { "embedding": [ ... ] } ] }
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            && data[0].TryGetProperty("embedding", out var arr))
            return EmbeddingJson.ToFloats(arr);
        return null;
    }
}

public sealed class OpenAiProvider : IAiProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com";
    public const string DefaultModel = "gpt-4o-mini";
    public const string DefaultEmbeddingModel = "text-embedding-3-small";

    public string Name => "openai";

    public string Description => "Hosted OpenAI (opt-in; needs an API key, makes outbound calls).";

    public IAiAnalyzer Create(AiConfig config) =>
        new OpenAiAnalyzer(config.BaseUrl ?? DefaultBaseUrl, config.Model ?? DefaultModel, RequireKey(config));

    public IAiEmbedder CreateEmbedder(AiConfig config) =>
        new OpenAiEmbedder(
            config.BaseUrl ?? DefaultBaseUrl,
            config.EmbeddingModel ?? DefaultEmbeddingModel,
            RequireKey(config),
            config.EmbeddingDimensions);

    private static string RequireKey(AiConfig config)
    {
        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"The openai AI provider needs an API key in the {config.ApiKeyEnv} environment variable.");
        return key;
    }
}
