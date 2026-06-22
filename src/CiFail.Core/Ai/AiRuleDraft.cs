using System.Text.Json;
using CiFail.Core.Models;

namespace CiFail.Core.Ai;

/// <summary>What an <see cref="IAiRuleDrafter"/> is given to draft a rule for an unmatched log (R23).</summary>
public sealed record AiRuleDraftRequest
{
    /// <summary>The relevant slice of the (normalized) log to reason about.</summary>
    public required string LogExcerpt { get; init; }

    /// <summary>Detected ecosystem — the drafted rule should usually target it.</summary>
    public required Ecosystem Ecosystem { get; init; }

    /// <summary>An existing low-confidence match, if any — the draft should beat it.</summary>
    public RuleMatch? WeakMatch { get; init; }
}

/// <summary>
/// A rule the AI drafted (R23). Mirrors the rule-pack schema fields, minus confidence — the
/// tool fixes a conservative confidence itself rather than letting the model assert one.
/// This is a <em>proposal</em>; <see cref="Rules.RuleDraftValidator"/> validates it before use.
/// </summary>
public sealed record AiRuleDraft
{
    public string Id { get; init; } = "";
    public string Ecosystem { get; init; } = "generic";
    public string Category { get; init; } = "general";
    public string Title { get; init; } = "";
    public string Match { get; init; } = "";
    public string Fix { get; init; } = "";
    public string? Docs { get; init; }

    /// <summary>Which model produced the draft (provenance).</summary>
    public string Model { get; init; } = "";
}

/// <summary>
/// Optional AI capability (R23): draft a rule from a log. A side-interface like
/// <see cref="IAiEmbedder"/> — providers opt in without changing the core <see cref="IAiAnalyzer"/>
/// contract. Best-effort: returns null when it can't produce a usable draft.
/// </summary>
public interface IAiRuleDrafter
{
    AiRuleDraft? DraftRule(AiRuleDraftRequest request);
}

/// <summary>Lenient parsing of a model's rule-draft JSON response (mirrors <see cref="AiPrompt.Parse"/>).</summary>
public static class AiRuleDraftParser
{
    /// <summary>Parse the model text into a draft, or null if no usable JSON (needs at least match or title).</summary>
    public static AiRuleDraft? Parse(string model, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var json = AiPrompt.ExtractJsonObject(text);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var match = AiPrompt.GetStr(root, "match");
            var title = AiPrompt.GetStr(root, "title");
            if (string.IsNullOrWhiteSpace(match) && string.IsNullOrWhiteSpace(title))
                return null;

            return new AiRuleDraft
            {
                Id = AiPrompt.GetStr(root, "id") ?? "",
                Ecosystem = AiPrompt.GetStr(root, "ecosystem") ?? "generic",
                Category = AiPrompt.GetStr(root, "category") ?? "general",
                Title = title ?? "",
                Match = match ?? "",
                Fix = AiPrompt.GetStr(root, "fix") ?? "",
                Docs = AiPrompt.GetStr(root, "docs"),
                Model = model,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
