using System.Text.RegularExpressions;
using CiFail.Core.Models;
using YamlDotNet.Core;

namespace CiFail.Core.Rules;

/// <summary>Severity of a <see cref="RuleDiagnostic"/>.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Validation failure — makes a pack unusable; <c>rules validate</c> exits non-zero.</summary>
    Error,

    /// <summary>Non-fatal advisory (e.g. a missing-but-optional field, or an intentional override).</summary>
    Warning,
}

/// <summary>Which tier a validated document came from.</summary>
public enum RulePackTier
{
    /// <summary>A pack embedded in the assembly (the shipped defaults).</summary>
    Embedded,

    /// <summary>A user/explicit pack (e.g. <c>~/.cifail/rules</c> or a path passed to validate).</summary>
    User,
}

/// <summary>A single problem found while validating rule packs.</summary>
/// <param name="Severity">Error (fatal) or Warning (advisory).</param>
/// <param name="Source">The pack the problem was found in (file name or resource).</param>
/// <param name="RuleId">The offending rule id, when known.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record RuleDiagnostic(DiagnosticSeverity Severity, string Source, string? RuleId, string Message);

/// <summary>A rule-pack document (source + raw YAML + tier) handed to the validator.</summary>
public sealed record RulePackDocument(string Source, RulePackTier Tier, string Yaml);

/// <summary>
/// Validates rule packs and reports diagnostics instead of silently skipping bad rules
/// (which is what the normal load path does). Powers <c>cifail rules validate</c> and the
/// CI lint step that guards the shipped packs.
/// </summary>
public static class RulePackValidator
{
    /// <summary>The result of validating a set of documents.</summary>
    /// <param name="Diagnostics">Every problem found, errors and warnings.</param>
    /// <param name="RuleCount">How many well-formed rules were seen across the documents.</param>
    public sealed record Result(IReadOnlyList<RuleDiagnostic> Diagnostics, int RuleCount)
    {
        public int ErrorCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        public int WarningCount => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        public bool HasErrors => ErrorCount > 0;
    }

    /// <summary>Validate the embedded packs plus the default (or a given) user rules directory.</summary>
    public static Result ValidateAll(string? userRulesDir = null)
    {
        var docs = new List<RulePackDocument>();

        foreach (var (source, yaml) in RulePackLoader.EmbeddedDocuments())
            docs.Add(new RulePackDocument(source, RulePackTier.Embedded, yaml));

        docs.AddRange(ReadDirectory(userRulesDir ?? RulePackLoader.DefaultUserRulesDir, RulePackTier.User));

        return Validate(docs);
    }

    /// <summary>Validate only the <c>*.yaml</c> packs under an explicit directory (CI lint of the repo's own packs).</summary>
    public static Result ValidatePath(string path)
        => Validate(ReadDirectory(path, RulePackTier.User).ToList());

    /// <summary>Validate an explicit set of documents.</summary>
    public static Result Validate(IReadOnlyList<RulePackDocument> documents)
    {
        var diagnostics = new List<RuleDiagnostic>();
        // id -> the tiers/sources it was defined in, to tell duplicates from overrides.
        var seen = new Dictionary<string, List<(RulePackTier Tier, string Source)>>(StringComparer.OrdinalIgnoreCase);
        var ruleCount = 0;

        foreach (var doc in documents)
        {
            List<RuleDefinition> rules;
            try
            {
                rules = RulePackLoader.DeserializeRaw(doc.Yaml).ToList();
            }
            catch (YamlException ex)
            {
                diagnostics.Add(new RuleDiagnostic(DiagnosticSeverity.Error, doc.Source, null,
                    $"could not parse YAML: {ex.Message}"));
                continue;
            }

            foreach (var rule in rules)
            {
                ruleCount++;
                ValidateRule(rule, doc, diagnostics);

                if (string.IsNullOrWhiteSpace(rule.Id))
                    continue;

                if (!seen.TryGetValue(rule.Id, out var defs))
                    seen[rule.Id] = defs = new List<(RulePackTier, string)>();
                defs.Add((doc.Tier, doc.Source));
            }
        }

        foreach (var (id, defs) in seen)
        {
            // A duplicate within the same tier is an error; an embedded rule re-declared in
            // the user tier is a legitimate override (advisory only).
            foreach (var tierGroup in defs.GroupBy(d => d.Tier))
            {
                if (tierGroup.Count() > 1)
                    diagnostics.Add(new RuleDiagnostic(DiagnosticSeverity.Error,
                        string.Join(", ", tierGroup.Select(d => d.Source)), id,
                        $"duplicate rule id '{id}' defined {tierGroup.Count()} times in the same tier"));
            }

            if (defs.Any(d => d.Tier == RulePackTier.Embedded) && defs.Any(d => d.Tier == RulePackTier.User))
                diagnostics.Add(new RuleDiagnostic(DiagnosticSeverity.Warning,
                    defs.First(d => d.Tier == RulePackTier.User).Source, id,
                    $"user rule '{id}' overrides an embedded rule of the same id"));
        }

        return new Result(diagnostics, ruleCount);
    }

    private static void ValidateRule(RuleDefinition rule, RulePackDocument doc, List<RuleDiagnostic> diagnostics)
    {
        var id = string.IsNullOrWhiteSpace(rule.Id) ? null : rule.Id;

        void Error(string m) => diagnostics.Add(new RuleDiagnostic(DiagnosticSeverity.Error, doc.Source, id, m));
        void Warn(string m) => diagnostics.Add(new RuleDiagnostic(DiagnosticSeverity.Warning, doc.Source, id, m));

        if (string.IsNullOrWhiteSpace(rule.Id))
            Error("rule is missing a required 'id'");

        if (string.IsNullOrWhiteSpace(rule.Match))
            Error("rule is missing a required 'match' pattern");
        else
        {
            try
            {
                _ = new Regex(rule.Match);
            }
            catch (ArgumentException ex)
            {
                Error($"'match' is not a valid regex: {ex.Message}");
            }
        }

        if (rule.Confidence is <= 0 or > 1)
            Error($"'confidence' must be in (0, 1]; got {rule.Confidence:0.###}");

        if (string.IsNullOrWhiteSpace(rule.Title))
            Warn("rule has no 'title'");

        if (string.IsNullOrWhiteSpace(rule.Fix))
            Warn("rule has no 'fix' guidance");

        // A rule tells you what broke and how to fix it; 'docs' is where you go when the fix
        // text isn't enough. Every shipped rule has one, and this keeps new ones from skipping
        // it — 53 of 91 had none before, including the entire generic pack.
        if (string.IsNullOrWhiteSpace(rule.Docs))
            Warn("rule has no 'docs' link (point at the tool's own reference page for this error)");
        else if (!Uri.TryCreate(rule.Docs, UriKind.Absolute, out var docs) ||
                 (docs.Scheme != Uri.UriSchemeHttp && docs.Scheme != Uri.UriSchemeHttps))
            Warn($"'docs' should be an absolute http(s) URL; got '{rule.Docs}'");
    }

    private static IEnumerable<RulePackDocument> ReadDirectory(string dir, RulePackTier tier)
    {
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
            yield return new RulePackDocument(Path.GetFileName(file), tier, File.ReadAllText(file));
    }
}
