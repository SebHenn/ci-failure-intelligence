using System.Text.RegularExpressions;
using CiFail.Core.Ai;
using CiFail.Core.Ingest;
using CiFail.Core.Models;

namespace CiFail.Core.Rules;

/// <summary>The outcome of validating an AI-drafted rule (R23).</summary>
/// <param name="Ok">True only when there are no error-severity diagnostics.</param>
/// <param name="Rule">The normalized, ready-to-write rule (conservative confidence forced).</param>
/// <param name="MatchedLine">The log line the drafted regex actually matched (empty if it didn't).</param>
/// <param name="Diagnostics">Every problem found, errors and warnings.</param>
public sealed record DraftValidation(
    bool Ok, RuleDefinition Rule, string MatchedLine, IReadOnlyList<RuleDiagnostic> Diagnostics);

/// <summary>
/// Validates an <see cref="AiRuleDraft"/> before it is shown or written (R23). The AI only drafts;
/// this is the gate. Beyond the standard pack lint (<see cref="RulePackValidator"/>) it enforces the
/// draft-specific safety rules: the regex must compile within a timeout (guards ReDoS), must actually
/// match the log it was drafted from (guards hallucination), and must not be overbroad.
/// </summary>
public static partial class RuleDraftValidator
{
    /// <summary>Confidence assigned to a drafted rule — the model never gets to assert its own.</summary>
    public const double DraftConfidence = 0.5;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private const int MaxMatchedLines = 20;
    private const string Source = "ai-draft";

    public static DraftValidation Validate(AiRuleDraft draft, LogDocument log)
    {
        var diagnostics = new List<RuleDiagnostic>();
        void Error(string m) => diagnostics.Add(new RuleDiagnostic(DiagnosticSeverity.Error, Source, NullIfBlank(draft.Id), m));

        // Normalize into a real rule with a forced confidence, then run the standard pack lint
        // (missing id/match, invalid regex, bad confidence, missing title/fix) — reuse, don't re-derive.
        var rule = ToRule(draft);
        var lint = RulePackValidator.Validate(new[]
        {
            new RulePackDocument(Source, RulePackTier.User, RulePackLoader.Serialize(new[] { rule })),
        });
        diagnostics.AddRange(lint.Diagnostics);

        // Draft-specific guard 1: kebab-case id (the standard lint only checks presence).
        if (!string.IsNullOrWhiteSpace(rule.Id) && !KebabId().IsMatch(rule.Id))
            Error($"id '{rule.Id}' must be kebab-case (lowercase letters, digits, hyphens)");

        // Draft-specific guard 2: overbroad patterns that would match almost anything.
        if (IsOverbroad(rule.Match))
            Error("match is too broad (it would match almost any log) — anchor it on the distinctive error text");

        // Draft-specific guards 3 & 4: must actually match the log, within a time budget (ReDoS).
        var matchedLine = "";
        Regex? regex = null;
        try { regex = new Regex(rule.Match, RegexOptions.IgnoreCase | RegexOptions.Multiline, RegexTimeout); }
        catch (ArgumentException) { /* invalid-regex already reported by the lint */ }

        if (regex is not null && !IsOverbroad(rule.Match))
        {
            try
            {
                var matches = regex.Matches(log.NormalizedText);
                if (matches.Count == 0)
                    Error("the regex does not match this log — the rule would never fire on this failure");
                else
                {
                    matchedLine = LineAt(log.NormalizedText, matches[0].Index);
                    if (matches.Count > MaxMatchedLines)
                        Error($"the regex matches {matches.Count} places in the log — make it more specific");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                Error("the regex timed out (possible catastrophic backtracking) — simplify it");
            }
        }

        var ok = diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
        return new DraftValidation(ok, rule, matchedLine, diagnostics);
    }

    private static RuleDefinition ToRule(AiRuleDraft d) => new()
    {
        Id = (d.Id ?? "").Trim().ToLowerInvariant(),
        Ecosystem = NormalizeEcosystem(d.Ecosystem),
        Category = string.IsNullOrWhiteSpace(d.Category) ? "general" : d.Category.Trim().ToLowerInvariant(),
        Title = (d.Title ?? "").Trim(),
        Match = (d.Match ?? "").Trim(),
        Confidence = DraftConfidence,
        Fix = (d.Fix ?? "").Trim(),
        Docs = string.IsNullOrWhiteSpace(d.Docs) ? null : d.Docs!.Trim(),
    };

    /// <summary>Force the ecosystem to a known value; anything unrecognized becomes <c>generic</c>.</summary>
    private static string NormalizeEcosystem(string? value) =>
        EcosystemDetector.TryParse(value ?? "", out var eco) ? eco.ToString().ToLowerInvariant() : "generic";

    private static bool IsOverbroad(string? pattern)
    {
        var p = (pattern ?? "").Trim();
        if (p.Length == 0) return true;
        // Patterns that effectively match everything, with optional anchors/grouping.
        var stripped = p.Trim('^', '$', '(', ')', ' ');
        return stripped is "" or ".*" or ".+" or ".*?" or ".+?" or "[\\s\\S]*" or "[\\s\\S]+" or "(?s).*";
    }

    private static string LineAt(string text, int index)
    {
        if (text.Length == 0) return "";
        index = Math.Clamp(index, 0, text.Length - 1);
        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex KebabId();
}
