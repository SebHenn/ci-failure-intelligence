using System.Text.Json;
using System.Text.Json.Serialization;
using CiFail.Core.Ingest;
using CiFail.Core.Models;

namespace CiFail.Core.Output;

/// <summary>
/// Stable, machine-readable JSON contract for an <see cref="Analysis"/> — the shape
/// emitted by <c>cifail analyze --json</c> and returned by the <c>cifail serve</c>
/// HTTP API. Kept as an explicit DTO (rather than serializing the domain model) so the
/// contract can evolve independently of internal types. Property names are PascalCase
/// and the schema is considered public: keep it stable.
/// </summary>
public static class AnalysisJson
{
    /// <summary>Serializer options for the public JSON contract (indented, null-omitting).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Map a domain <see cref="Models.Analysis"/> to its public DTO.</summary>
    public static AnalysisDto ToDto(Models.Analysis analysis) => new()
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

    /// <summary>Serialize an analysis to the public JSON string.</summary>
    public static string Serialize(Models.Analysis analysis) =>
        JsonSerializer.Serialize(ToDto(analysis), Options);

    /// <summary>Parse the public JSON string back into a DTO.</summary>
    public static AnalysisDto? Deserialize(string json) =>
        JsonSerializer.Deserialize<AnalysisDto>(json, Options);

    /// <summary>
    /// Reconstruct a domain <see cref="Models.Analysis"/> from its DTO, so a result fetched from
    /// a remote <c>cifail serve</c> (R18) renders through the same <c>ConsoleRenderer</c> / <c>--json</c>
    /// path as a local one. <c>ToDto(FromDto(dto))</c> round-trips the public fields exactly.
    /// </summary>
    public static Models.Analysis FromDto(AnalysisDto d)
    {
        EcosystemDetector.TryParse(d.Ecosystem, out var ecosystem); // unknown -> Ecosystem.Unknown
        var sep = d.Fingerprint.IndexOf(':');
        var fingerprint = sep < 0
            ? new FailureFingerprint { RuleId = d.Fingerprint, Hash = "" }
            : new FailureFingerprint { RuleId = d.Fingerprint[..sep], Hash = d.Fingerprint[(sep + 1)..] };

        return new Models.Analysis
        {
            Source = d.Source,
            Ecosystem = ecosystem,
            Matches = d.Matches.Select(FromMatchDto).ToList(),
            Fingerprint = fingerprint,
            HistoryId = d.HistoryId,
            AnalyzedAt = d.AnalyzedAt,
            SimilarFailures = d.SimilarFailures.Select(s => new SimilarFailure
            {
                Id = s.Id,
                Similarity = s.Similarity,
                RuleId = s.RuleId,
                AnalyzedAt = s.AnalyzedAt,
                Resolution = s.Resolution,
            }).ToList(),
            AiSuggestion = d.Ai is { } ai
                ? new AiSuggestion { Model = ai.Model, RootCause = ai.RootCause, Fix = ai.Fix }
                : null,
        };
    }

    private static RuleMatch FromMatchDto(MatchDto m) => new()
    {
        Rule = new RuleDefinition
        {
            Id = m.RuleId,
            Title = m.Title,
            Category = m.Category,
            Confidence = m.Confidence,
            Match = string.Empty, // the regex isn't part of the public contract
            Fix = m.Fix,
            Docs = m.Docs,
            Severity = m.Severity,
        },
        Captures = m.Captures is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(m.Captures),
        MatchedLine = m.MatchedLine,
        Score = m.Confidence,
        Fix = m.Fix,
        LineNumber = m.LineNumber ?? 0,
        ContextBefore = m.ContextBefore ?? (IReadOnlyList<string>)Array.Empty<string>(),
        ContextAfter = m.ContextAfter ?? (IReadOnlyList<string>)Array.Empty<string>(),
        OccurrenceCount = m.OccurrenceCount ?? 1,
    };

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

        // Additive (R34). Null rather than 0/[] when absent so `WhenWritingNull` keeps them out
        // of the document entirely and an older consumer sees exactly what it saw before.
        LineNumber = m.LineNumber > 0 ? m.LineNumber : null,
        ContextBefore = m.ContextBefore.Count > 0 ? m.ContextBefore.ToList() : null,
        ContextAfter = m.ContextAfter.Count > 0 ? m.ContextAfter.ToList() : null,
        OccurrenceCount = m.OccurrenceCount > 1 ? m.OccurrenceCount : null,
        Severity = m.Rule.Severity,
    };

    public sealed class AnalysisDto
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

    public sealed class MatchDto
    {
        public string RuleId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public double Confidence { get; init; }
        public string MatchedLine { get; init; } = "";
        public string Fix { get; init; } = "";
        public string? Docs { get; init; }
        public Dictionary<string, string>? Captures { get; init; }

        /// <summary>1-based line of <see cref="MatchedLine"/> in the normalized log; null when unknown.</summary>
        public int? LineNumber { get; init; }

        /// <summary>Lines immediately before the matched line. Omitted when empty.</summary>
        public List<string>? ContextBefore { get; init; }

        /// <summary>Lines immediately after the matched line. Omitted when empty.</summary>
        public List<string>? ContextAfter { get; init; }

        /// <summary>
        /// How many times the rule matched. Omitted when 1, so the common case of a single
        /// occurrence doesn't grow every document.
        /// </summary>
        public int? OccurrenceCount { get; init; }

        /// <summary>
        /// The rule's declared severity (R36) — <c>error</c>/<c>warning</c>/<c>note</c>, distinct
        /// from <see cref="Confidence"/>. Omitted when the rule doesn't declare one, in which case
        /// consumers should fall back to the confidence buckets as before.
        /// </summary>
        public string? Severity { get; init; }
    }

    public sealed class SimilarDto
    {
        public long Id { get; init; }
        public double Similarity { get; init; }
        public string RuleId { get; init; } = "";
        public DateTimeOffset AnalyzedAt { get; init; }
        public string? Resolution { get; init; }
    }

    public sealed class AiDto
    {
        public string Model { get; init; } = "";
        public string RootCause { get; init; } = "";
        public string Fix { get; init; } = "";
    }
}
