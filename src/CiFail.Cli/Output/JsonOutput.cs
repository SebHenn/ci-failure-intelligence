using System.Text.Json;
using System.Text.Json.Serialization;
using CiFail.Core.Models;

namespace CiFail.Cli.Output;

/// <summary>
/// Stable, machine-readable JSON contract for `cifail analyze --json`. Kept as an
/// explicit DTO (rather than serializing the domain model) so the CLI output schema
/// can evolve independently of internal types.
/// </summary>
public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Analysis analysis)
    {
        var dto = new AnalysisDto
        {
            Source = analysis.Source,
            Ecosystem = analysis.Ecosystem.ToString().ToLowerInvariant(),
            AnalyzedAt = analysis.AnalyzedAt,
            Matched = analysis.HasMatch,
            HistoryId = analysis.HistoryId,
            Fingerprint = analysis.Fingerprint.ToString(),
            RootCause = analysis.RootCause is { } rc ? ToMatchDto(rc) : null,
            Matches = analysis.Matches.Select(ToMatchDto).ToList(),
            SimilarFailures = analysis.SimilarFailures.Select(s => new SimilarDto
            {
                Id = s.Id,
                Similarity = Math.Round(s.Similarity, 4),
                RuleId = s.RuleId,
                AnalyzedAt = s.AnalyzedAt,
                Resolution = s.Resolution,
            }).ToList(),
            Ai = analysis.AiSuggestion is { } ai ? new AiDto
            {
                Model = ai.Model,
                RootCause = ai.RootCause,
                Fix = ai.Fix,
            } : null,
        };

        return JsonSerializer.Serialize(dto, Options);
    }

    private static MatchDto ToMatchDto(RuleMatch m) => new()
    {
        RuleId = m.Rule.Id,
        Title = m.Rule.Title,
        Category = m.Rule.Category,
        Confidence = Math.Round(m.Score, 4),
        MatchedLine = m.MatchedLine,
        Fix = m.Fix,
        Docs = m.Rule.Docs,
        Captures = m.Captures.Count > 0 ? new Dictionary<string, string>(m.Captures) : null,
    };

    private sealed class AnalysisDto
    {
        public string Source { get; init; } = "";
        public string Ecosystem { get; init; } = "";
        public DateTimeOffset AnalyzedAt { get; init; }
        public bool Matched { get; init; }
        public long? HistoryId { get; init; }
        public string Fingerprint { get; init; } = "";
        public MatchDto? RootCause { get; init; }
        public List<MatchDto> Matches { get; init; } = new();
        public List<SimilarDto> SimilarFailures { get; init; } = new();
        public AiDto? Ai { get; init; }
    }

    private sealed class MatchDto
    {
        public string RuleId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public double Confidence { get; init; }
        public string MatchedLine { get; init; } = "";
        public string Fix { get; init; } = "";
        public string? Docs { get; init; }
        public Dictionary<string, string>? Captures { get; init; }
    }

    private sealed class SimilarDto
    {
        public long Id { get; init; }
        public double Similarity { get; init; }
        public string RuleId { get; init; } = "";
        public DateTimeOffset AnalyzedAt { get; init; }
        public string? Resolution { get; init; }
    }

    private sealed class AiDto
    {
        public string Model { get; init; } = "";
        public string RootCause { get; init; } = "";
        public string Fix { get; init; } = "";
    }
}
