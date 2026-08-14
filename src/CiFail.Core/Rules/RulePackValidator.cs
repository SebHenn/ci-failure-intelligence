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

    /// <summary>
    /// Validate the embedded packs plus the user packs: a single given directory, or — with none
    /// given — every location on the <see cref="RuleSearchPath"/>, which is what actually loads.
    /// </summary>
    public static Result ValidateAll(string? userRulesDir = null)
    {
        var docs = new List<RulePackDocument>();

        foreach (var (source, yaml) in RulePackLoader.EmbeddedDocuments())
            docs.Add(new RulePackDocument(source, RulePackTier.Embedded, yaml));

        var dirs = userRulesDir is null
            ? RuleSearchPath.Resolve()
            : new[] { userRulesDir };

        // With more than one directory in play, a bare file name no longer identifies a pack —
        // two repos can both ship `rules.yaml`, and "duplicate id in rules.yaml, rules.yaml"
        // helps nobody.
        foreach (var dir in dirs)
            docs.AddRange(ReadDirectory(dir, RulePackTier.User, qualify: dirs.Count > 1));

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
                ValidatePlaceholders(rule, Error);
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

    /// <summary>
    /// Every <c>{name}</c> in a rule's <c>fix</c> must be captured by <b>every</b> top-level
    /// alternation branch of its <c>match</c>.
    ///
    /// <para>
    /// The obvious version of this check — "the placeholder names a group that exists somewhere in
    /// the pattern" — is useless here, and eleven shipped rules proved it. A rule like
    /// <c>A(?&lt;file&gt;…)|B</c> defines <c>file</c>, so that check passes, yet a log matching the
    /// second branch leaves <c>{file}</c> in the rendered text and the user is shown the template
    /// instead of the answer. The branch, not the pattern, is the unit that has to satisfy the fix.
    /// </para>
    ///
    /// <para>
    /// Two shapes hit this: a second branch whose groups were given <c>2</c> suffixes to dodge
    /// duplicate names (.NET permits duplicates — they are the fix), and a branch that carries no
    /// captures at all because the tool simply doesn't print that detail in that wording. For the
    /// latter, the answer is to write a fix that doesn't need the capture.
    /// </para>
    /// </summary>
    private static void ValidatePlaceholders(RuleDefinition rule, Action<string> error)
    {
        if (string.IsNullOrWhiteSpace(rule.Fix)) return;

        var placeholders = PlaceholderPattern.Matches(rule.Fix)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (placeholders.Count == 0) return;

        foreach (var branch in SplitTopLevelAlternation(rule.Match))
        {
            var groups = GroupNamePattern.Matches(branch)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var missing = placeholders.Where(p => !groups.Contains(p)).ToList();
            if (missing.Count == 0) continue;

            error($"'fix' uses {{{string.Join("}, {", missing)}}} but the pattern alternative " +
                  $"`{Elide(branch)}` does not capture " +
                  (missing.Count == 1 ? "it" : "them") +
                  " — a log matching that alternative would show the placeholder literally");
        }
    }

    private static readonly Regex PlaceholderPattern = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex GroupNamePattern = new(@"\(\?<(\w+)>", RegexOptions.Compiled);

    /// <summary>
    /// Split a pattern on its top-level <c>|</c> only — ignoring one inside a group, inside a
    /// character class, or escaped. Regex-splitting a regex would get every one of those wrong.
    /// </summary>
    internal static IReadOnlyList<string> SplitTopLevelAlternation(string pattern)
    {
        var branches = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var inClass = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '\\' && i + 1 < pattern.Length)
            {
                current.Append(c).Append(pattern[i + 1]);
                i++;
                continue;
            }

            switch (c)
            {
                case '[' when !inClass: inClass = true; break;
                case ']' when inClass: inClass = false; break;
                case '(' when !inClass: depth++; break;
                case ')' when !inClass && depth > 0: depth--; break;
                case '|' when !inClass && depth == 0:
                    branches.Add(current.ToString());
                    current.Clear();
                    continue;
            }

            current.Append(c);
        }

        branches.Add(current.ToString());
        return branches;
    }

    private static string Elide(string branch) =>
        branch.Length <= 60 ? branch : branch[..57] + "...";

    private static IEnumerable<RulePackDocument> ReadDirectory(string dir, RulePackTier tier, bool qualify = false)
    {
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
            yield return new RulePackDocument(qualify ? file : Path.GetFileName(file), tier, File.ReadAllText(file));
    }
}
