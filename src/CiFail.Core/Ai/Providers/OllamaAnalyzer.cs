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
public sealed class OllamaAnalyzer : IAiAnalyzer
{
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaAnalyzer(string baseUrl, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public AiSuggestion? Suggest(AiRequest request)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var payload = new
        {
            model = _model,
            prompt = AiPrompt.Build(request),
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
        var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
        return AiPrompt.Parse($"ollama/{_model}", text);
    }
}

public sealed class OllamaProvider : IAiProvider
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "llama3.2";

    public string Name => "ollama";

    public string Description => "Local Ollama model (default, fully offline; needs Ollama running).";

    public IAiAnalyzer Create(AiConfig config) =>
        new OllamaAnalyzer(config.BaseUrl ?? DefaultBaseUrl, config.Model ?? DefaultModel);
}
