using System.Text.Json;
using CiFail.Core.Models;

namespace CiFail.Core.Output;

/// <summary>
/// Machine-readable view of the loaded rule inventory — what <c>cifail rules list --json</c> and
/// <c>cifail rules explain --json</c> emit.
///
/// <para>
/// An explicit DTO rather than serializing <see cref="RuleDefinition"/> directly, for the same
/// reason <see cref="AnalysisJson"/> is: the YAML schema is an authoring format that can change
/// shape, while this is a contract other tools read. PascalCase, matching every other
/// <c>--json</c> document cifail produces.
/// </para>
/// </summary>
public static class RulesJson
{
    public static string Serialize(IEnumerable<RuleDefinition> rules, IReadOnlyList<string> searchPath)
    {
        // Materialize once: the argument is usually a LINQ chain over the loaded packs, and
        // enumerating it twice would re-run the ordering for the count.
        var dtos = rules.Select(r => ToDto(r)).ToList();

        return JsonSerializer.Serialize(
            new RuleInventoryDto
            {
                Count = dtos.Count,
                SearchPath = searchPath.ToList(),
                Rules = dtos,
            },
            AnalysisJson.Options);
    }

    public static string Serialize(RuleDefinition rule, string source) =>
        JsonSerializer.Serialize(ToDto(rule, source), AnalysisJson.Options);

    public static RuleDto ToDto(RuleDefinition r, string? source = null) => new()
    {
        Id = r.Id,
        Ecosystems = r.AllEcosystems.ToList(),
        Category = r.Category,
        Title = r.Title,
        Confidence = Math.Round(r.Confidence, 4),
        Docs = r.Docs,
        Source = source,

        // R36 fields: omitted when the rule doesn't set them, so a consumer can tell "not
        // declared" from "declared as the default".
        Severity = r.Severity,
        Requires = r.Requires,
        NotMatch = r.NotMatch,
        Enabled = r.Enabled ? null : false,

        // The pattern is included here, unlike in AnalysisJson: `rules` is about the rules
        // themselves, and "what does this actually match?" is the question being asked.
        Match = r.Match,
        Fix = r.Fix,
    };

    public sealed class RuleInventoryDto
    {
        public int Count { get; init; }

        /// <summary>Directories consulted, most general first — later wins on a duplicate id.</summary>
        public List<string> SearchPath { get; init; } = new();

        public List<RuleDto> Rules { get; init; } = new();
    }

    public sealed class RuleDto
    {
        public string Id { get; init; } = "";
        public List<string> Ecosystems { get; init; } = new();
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public double Confidence { get; init; }
        public string Match { get; init; } = "";
        public string Fix { get; init; } = "";
        public string? Docs { get; init; }
        public string? Severity { get; init; }
        public string? Requires { get; init; }
        public string? NotMatch { get; init; }

        /// <summary>Null when the rule is enabled (the norm); false when it has been switched off.</summary>
        public bool? Enabled { get; init; }

        /// <summary>Where the rule came from — set by <c>rules explain</c>, null in a listing.</summary>
        public string? Source { get; init; }
    }
}
