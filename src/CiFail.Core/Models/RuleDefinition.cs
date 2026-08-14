namespace CiFail.Core.Models;

/// <summary>
/// A single failure pattern loaded from a YAML rule pack. Deserialized directly
/// from YAML, so property names mirror the documented rule-pack schema.
/// </summary>
public sealed class RuleDefinition
{
    /// <summary>Stable unique identifier, e.g. "nuget-nu1101".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Ecosystem this rule belongs to (e.g. "dotnet", "node", "generic").</summary>
    public string Ecosystem { get; set; } = "generic";

    /// <summary>Loose grouping, e.g. "dependency", "compile", "test".</summary>
    public string Category { get; set; } = "general";

    /// <summary>Short human-readable summary of the failure.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Regular expression matched against the normalized log. May contain named
    /// groups (e.g. <c>(?&lt;package&gt;...)</c>) that are interpolated into <see cref="Fix"/>.
    /// </summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Confidence (0..1) assigned to a match of this rule.</summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>
    /// Fix guidance template. <c>{name}</c> placeholders are replaced with the
    /// corresponding named capture group from <see cref="Match"/>.
    /// </summary>
    public string Fix { get; set; } = string.Empty;

    /// <summary>Optional documentation link.</summary>
    public string? Docs { get; set; }

    /// <summary>
    /// Additional ecosystems this rule applies to, beyond <see cref="Ecosystem"/>.
    ///
    /// <para>
    /// For the genuinely cross-cutting rule — a Docker failure that shows up in a Node build as
    /// readily as a Go one — where the alternatives were duplicating the rule under a second id
    /// or demoting it to <c>generic</c> and having it fire on everything.
    /// </para>
    ///
    /// <para>
    /// This deliberately does <b>not</b> replace <c>RuleEngine.Inherits</c> (Android→Java,
    /// Scala→Java). That table says something about the <i>ecosystems</i> — an Android build is a
    /// JVM build — not about any individual rule, and expressing it here would mean tagging every
    /// JVM rule with <c>[java, android, scala]</c> and keeping all of them in step, which is the
    /// same duplication the table exists to avoid.
    /// </para>
    /// </summary>
    public List<string> Ecosystems { get; set; } = new();

    /// <summary>
    /// How bad the failure is, as distinct from <see cref="Confidence"/> (how sure we are it is
    /// what happened). One of <c>error</c>, <c>warning</c>, <c>note</c>; null falls back to the
    /// confidence buckets, which is what every rule did before this field existed.
    ///
    /// <para>
    /// Confidence was doing both jobs, which is why a report could not distinguish a
    /// confidently-identified cosmetic warning from a tentatively-identified fatal one.
    /// </para>
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>
    /// Optional regex that <b>suppresses</b> this rule when it also matches the log.
    ///
    /// <para>
    /// Expressing "not this" previously meant a negative lookahead inside the one pattern —
    /// <c>go-build-failed</c> does exactly that, and it is unreadable. A separate field says what
    /// it means.
    /// </para>
    /// </summary>
    public string? NotMatch { get; set; }

    /// <summary>
    /// Optional regex that must <b>also</b> appear somewhere in the log for this rule to fire.
    /// Lets a rule be precise about context a single pattern cannot express without heroics —
    /// "this stack trace, but only in a test run".
    /// </summary>
    public string? Requires { get; set; }

    /// <summary>
    /// Set <c>false</c> to switch a rule off. The point is turning off a <i>shipped</i> rule: a
    /// user pack redefining the same id wins, so a three-line entry with <c>enabled: false</c>
    /// silences it. Previously the only route was overriding the rule with a pattern that matches
    /// nothing, because the validator rejects <c>confidence: 0</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Every ecosystem this rule applies to, deduplicated.</summary>
    public IEnumerable<string> AllEcosystems =>
        Ecosystems.Count == 0
            ? new[] { Ecosystem }
            : Ecosystems.Prepend(Ecosystem).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The <c>severity</c> vocabulary, mirroring SARIF's levels.</summary>
public static class RuleSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Note = "note";

    public static readonly IReadOnlyList<string> All = new[] { Error, Warning, Note };

    public static bool IsValid(string? value) =>
        value is null || All.Contains(value.Trim().ToLowerInvariant());
}
