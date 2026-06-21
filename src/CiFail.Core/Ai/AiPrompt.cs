using System.Text;
using System.Text.Json;
using CiFail.Core.Models;

namespace CiFail.Core.Ai;

/// <summary>Shared prompt construction + lenient response parsing for AI analyzers.</summary>
public static class AiPrompt
{
    /// <summary>Build the instruction prompt sent to the model.</summary>
    public static string Build(AiRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a CI/build/test failure assistant.");
        sb.AppendLine("Given the log excerpt below, identify the single most likely root cause and a concrete fix.");
        sb.AppendLine("Respond ONLY with a JSON object of the form {\"rootCause\": \"...\", \"fix\": \"...\"}.");
        sb.AppendLine("Keep each field concise (1-3 sentences). Do not include any text outside the JSON.");
        sb.AppendLine();
        sb.AppendLine($"Ecosystem: {r.Ecosystem.ToString().ToLowerInvariant()}");
        if (r.TopMatch is not null)
            sb.AppendLine($"A rule weakly matched (\"{r.TopMatch.Rule.Title}\", {r.TopMatch.Rule.Category}); refine or correct it if needed.");
        sb.AppendLine();
        sb.AppendLine("Log excerpt:");
        sb.AppendLine(r.LogExcerpt);
        return sb.ToString();
    }

    /// <summary>
    /// Parse a model's text response into an <see cref="AiSuggestion"/>. Tolerant: extracts the
    /// first JSON object if the model wrapped it in prose; if no usable JSON is found, falls back
    /// to treating the whole response as the fix. Returns null only for empty input.
    /// </summary>
    public static AiSuggestion? Parse(string model, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var json = ExtractJsonObject(text);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var rootCause = GetStr(root, "rootCause") ?? GetStr(root, "root_cause");
                var fix = GetStr(root, "fix");
                if (!string.IsNullOrWhiteSpace(rootCause) || !string.IsNullOrWhiteSpace(fix))
                    return new AiSuggestion { Model = model, RootCause = rootCause ?? "", Fix = fix ?? "" };
            }
            catch (JsonException) { /* fall through to the prose fallback */ }
        }

        return new AiSuggestion { Model = model, RootCause = "AI suggestion", Fix = text.Trim() };
    }

    private static string? GetStr(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
