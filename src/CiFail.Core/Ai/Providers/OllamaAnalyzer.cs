using System.Text;
using System.Text.Json;
using CiFail.Core.Configuration;
using CiFail.Core.Models;

namespace CiFail.Core.Ai.Providers;

/// <summary>
/// Local Ollama analyzer (the default). Talks to <c>{baseUrl}/api/generate</c> with
/// <c>format: json</c> so the model returns a JSON object directly. Fully offline — nothing
/// leaves the machine.
/// </summary>
public sealed class OllamaAnalyzer : IAiAnalyzer, IAiRuleDrafter
{
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaAnalyzer(string baseUrl, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public AiSuggestion? Suggest(AiRequest request) =>
        AiPrompt.Parse($"ollama/{_model}", Generate(AiPrompt.Build(request)));

    public AiRuleDraft? DraftRule(AiRuleDraftRequest request) =>
        AiRuleDraftParser.Parse($"ollama/{_model}", Generate(AiPrompt.BuildRuleDraft(request)));

    /// <summary>POST a prompt to <c>/api/generate</c> with <c>format: json</c> and return the response text.</summary>
    private string? Generate(string prompt)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var payload = new
        {
            model = _model,
            prompt,
            stream = false,
            format = "json",
        };
        var http = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = client.Send(http);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(response.Content.ReadAsStream());
        return doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
    }
}

/// <summary>
/// Local Ollama text embedder (R10) via <c>{baseUrl}/api/embeddings</c>. Fully offline, like
/// the analyzer. Returns null on any failure so similarity falls back to TF-IDF.
/// </summary>
public sealed class OllamaEmbedder : IAiEmbedder
{
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaEmbedder(string baseUrl, string model, int dimensions)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        Dimensions = dimensions;
    }

    public int Dimensions { get; }

    public float[]? Embed(string text)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var payload = new { model = _model, prompt = text };
        var http = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = client.Send(http);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(response.Content.ReadAsStream());
        return doc.RootElement.TryGetProperty("embedding", out var arr)
            ? EmbeddingJson.ToFloats(arr)
            : null;
    }
}

public sealed class OllamaProvider : IAiProvider
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "llama3.2";
    public const string DefaultEmbeddingModel = "nomic-embed-text";

    public string Name => "ollama";

    public string Description => "Local Ollama model (default, fully offline; needs Ollama running).";

    public IAiAnalyzer Create(AiConfig config) =>
        new OllamaAnalyzer(config.BaseUrl ?? DefaultBaseUrl, config.Model ?? DefaultModel);

    public IAiEmbedder CreateEmbedder(AiConfig config) =>
        new OllamaEmbedder(
            config.BaseUrl ?? DefaultBaseUrl,
            config.EmbeddingModel ?? DefaultEmbeddingModel,
            config.EmbeddingDimensions);
}
