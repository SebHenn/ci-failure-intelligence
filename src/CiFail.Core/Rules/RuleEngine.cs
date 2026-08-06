using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CiFail.Core.Models;

namespace CiFail.Core.Rules;

/// <summary>
/// Matches a log against a set of rules and returns the matches ranked by score.
/// Rules are filtered to the detected ecosystem (plus ecosystem-agnostic "generic"
/// rules) before matching. Compiled regexes are cached per rule.
/// </summary>
public sealed class RuleEngine
{
    private readonly IReadOnlyList<RuleDefinition> _rules;
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    public RuleEngine(IReadOnlyList<RuleDefinition> rules) => _rules = rules;

    public IReadOnlyList<RuleDefinition> Rules => _rules;

    /// <summary>
    /// Evaluate all applicable rules against the log. Returns matches sorted by
    /// descending score (highest-confidence root cause first).
    /// </summary>
    public IReadOnlyList<RuleMatch> Match(LogDocument log, Ecosystem ecosystem)
    {
        var matches = new List<RuleMatch>();

        foreach (var rule in _rules)
        {
            if (!AppliesTo(rule, ecosystem)) continue;

            Regex regex;
            try
            {
                regex = _regexCache.GetOrAdd(rule.Id, _ =>
                    new Regex(rule.Match, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline));
            }
            catch (ArgumentException)
            {
                // A malformed rule regex should never crash analysis; skip it.
                continue;
            }

            var m = regex.Match(log.NormalizedText);
            if (!m.Success) continue;

            var captures = ExtractCaptures(regex, m);
            matches.Add(new RuleMatch
            {
                Rule = rule,
                Captures = captures,
                MatchedLine = MatchedLine(log.NormalizedText, m.Index),
                Score = rule.Confidence,
                Fix = Interpolate(rule.Fix, captures),
            });
        }

        return matches
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Rule.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Ecosystems that also inherit another's rules, because a build in the first genuinely
    /// <i>is</i> a build in the second.
    ///
    /// <para>
    /// An Android build is a Gradle/JVM build — which is why the detector ranks Android above
    /// Java in the first place. Without this, an Android job that ran out of memory got the
    /// vague <c>generic-oom</c> instead of <c>gradle-daemon-disappeared</c>, and Kotlin
    /// compile errors went unexplained on the platform where most Kotlin is written, purely
    /// because those rules live in java.yaml. The alternative — copying every JVM rule into
    /// android.yaml — is two copies to keep in step, which is how rules rot.
    /// </para>
    /// </summary>
    private static readonly Dictionary<Ecosystem, Ecosystem[]> Inherits = new()
    {
        [Ecosystem.Android] = new[] { Ecosystem.Java },
        [Ecosystem.Scala] = new[] { Ecosystem.Java },
    };

    private static bool AppliesTo(RuleDefinition rule, Ecosystem ecosystem)
    {
        if (string.Equals(rule.Ecosystem, "generic", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ecosystem is Ecosystem.Generic or Ecosystem.Unknown)
            return true; // when undetected, try everything
        if (string.Equals(rule.Ecosystem, ecosystem.ToString(), StringComparison.OrdinalIgnoreCase))
            return true;

        return Inherits.TryGetValue(ecosystem, out var parents)
            && parents.Any(p => string.Equals(rule.Ecosystem, p.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> ExtractCaptures(Regex regex, Match m)
    {
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in regex.GetGroupNames())
        {
            // Skip the implicit numbered groups; keep named ones only.
            if (int.TryParse(name, out _)) continue;
            var g = m.Groups[name];
            if (g.Success) captures[name] = g.Value.Trim();
        }
        return captures;
    }

    private static string MatchedLine(string text, int index)
    {
        if (text.Length == 0) return string.Empty;

        // The match can begin on a newline itself (e.g. a pattern starting with \s), so anchor on
        // the line start and search the line end forward from there — never backwards (which would
        // yield a negative-length slice).
        index = Math.Clamp(index, 0, text.Length - 1);
        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }

    /// <summary>Replace <c>{name}</c> placeholders with capture values.</summary>
    public static string Interpolate(string template, IReadOnlyDictionary<string, string> captures)
    {
        if (string.IsNullOrEmpty(template) || captures.Count == 0)
            return template;

        return Regex.Replace(template, @"\{(\w+)\}", m =>
            captures.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
