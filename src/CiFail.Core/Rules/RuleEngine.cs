using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CiFail.Core.Analysis;
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
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// How long any single rule pattern may run against one log before it is abandoned.
    ///
    /// <para>
    /// Rule packs are <i>untrusted input</i>. Since R14 cifail loads them from the
    /// <c>.cifail/rules</c> directory of whatever repository you happen to be working in, so
    /// running <c>cifail analyze</c> inside a freshly cloned checkout executes that repository's
    /// regexes against your log. Without a timeout a pattern with catastrophic backtracking —
    /// authored maliciously or, far more likely, by accident — hangs the CLI, and on
    /// <c>cifail serve</c> pins a request thread indefinitely.
    /// </para>
    ///
    /// <para>
    /// Two seconds is deliberately generous next to <see cref="RuleDraftValidator"/>'s one-second
    /// draft-time gate: this budget is spent against a real log that may be tens of megabytes,
    /// where that one is spent against a single excerpt.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public RuleEngine(IReadOnlyList<RuleDefinition> rules) => _rules = rules;

    public IReadOnlyList<RuleDefinition> Rules => _rules;

    /// <summary>
    /// Evaluate all applicable rules against the log. Returns matches sorted by
    /// descending score (highest-confidence root cause first).
    /// </summary>
    /// <param name="diagnostics">
    /// Optional sink for problems that made a rule unusable — a malformed pattern, or one that
    /// exceeded <see cref="MatchTimeout"/>. Analysis continues without that rule either way, but a
    /// rule that has silently stopped working is exactly the failure this codebase's fixture
    /// discipline exists to prevent, so the caller is given the chance to say so.
    /// </param>
    public IReadOnlyList<RuleMatch> Match(
        LogDocument log,
        Ecosystem ecosystem,
        ICollection<string>? diagnostics = null,
        int contextLines = AnalysisOptions.DefaultContextLines)
    {
        var matches = new List<RuleMatch>();

        foreach (var rule in _rules)
        {
            if (!AppliesTo(rule, ecosystem)) continue;

            Regex regex;
            try
            {
                // Keyed on the pattern, not the id: two rules can share an id (only
                // RulePackLoader dedupes, and a RuleEngine can be constructed directly), and
                // keying on the id made the second one silently run the first one's pattern.
                regex = _regexCache.GetOrAdd(rule.Match, pattern => new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline,
                    MatchTimeout));
            }
            catch (ArgumentException)
            {
                // A malformed rule regex should never crash analysis; skip it.
                diagnostics?.Add($"rule '{rule.Id}' has an invalid pattern and was skipped");
                continue;
            }

            Match m;
            int occurrences;
            try
            {
                m = regex.Match(log.NormalizedText);
                if (!m.Success) continue;

                // Count the rest too, capped: "12 of these" is a materially different report from
                // "one of these", and it used to be invisible. The cap keeps a rule that matches
                // every line of a 100k-line log from dominating the run.
                occurrences = CountOccurrences(regex, log.NormalizedText, m);
            }
            catch (RegexMatchTimeoutException)
            {
                diagnostics?.Add(
                    $"rule '{rule.Id}' took longer than {MatchTimeout.TotalSeconds:0.#}s to " +
                    "evaluate and was skipped");
                continue;
            }

            var captures = ExtractCaptures(regex, m);
            var line = LocateLine(log, m.Index);
            matches.Add(new RuleMatch
            {
                Rule = rule,
                Captures = captures,
                MatchedLine = line.Text,
                Score = rule.Confidence,
                Fix = Interpolate(rule.Fix, captures),
                LineNumber = line.Number,
                ContextBefore = Context(log.NormalizedLines, line.Number, contextLines, before: true),
                ContextAfter = Context(log.NormalizedLines, line.Number, contextLines, before: false),
                OccurrenceCount = occurrences,
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

    /// <summary>
    /// The line containing <paramref name="index"/>, with its 1-based number.
    ///
    /// <para>
    /// The number is the count of newlines before the line start — an O(n) scan of the prefix,
    /// paid once per matching rule rather than per line, which is cheap next to the regex that
    /// just ran over the whole text.
    /// </para>
    /// </summary>
    private static (string Text, int Number) LocateLine(LogDocument log, int index)
    {
        var text = log.NormalizedText;
        if (text.Length == 0) return (string.Empty, 0);

        // The match can begin on a newline itself (e.g. a pattern starting with \s), so anchor on
        // the line start and search the line end forward from there — never backwards (which would
        // yield a negative-length slice).
        index = Math.Clamp(index, 0, text.Length - 1);
        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;

        var number = 1;
        for (var i = 0; i < start; i++)
            if (text[i] == '\n') number++;

        return (text[start..end].Trim(), number);
    }

    /// <summary>
    /// Lines either side of a 1-based line number, in log order. Blank lines at the outer edge are
    /// trimmed — padding a panel with the empty lines a build tool happened to print is noise.
    /// </summary>
    private static IReadOnlyList<string> Context(
        IReadOnlyList<string> lines, int lineNumber, int count, bool before)
    {
        if (count <= 0 || lineNumber <= 0 || lines.Count == 0) return Array.Empty<string>();

        var index = lineNumber - 1; // to 0-based
        var window = before
            ? lines.Skip(Math.Max(0, index - count)).Take(Math.Min(count, index))
            : lines.Skip(index + 1).Take(count);

        var result = window.Select(l => l.TrimEnd()).ToList();

        if (before)
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[0])) result.RemoveAt(0);
        else
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);

        return result;
    }

    /// <summary>
    /// How many times the pattern matches, counted from the first match and capped at
    /// <see cref="MaxCountedOccurrences"/> so a rule that fires on every line of a huge log
    /// doesn't turn counting into the expensive part of analysis.
    /// </summary>
    private static int CountOccurrences(Regex regex, string text, Match first)
    {
        var count = 1;
        var next = first.NextMatch();
        while (next.Success && count < MaxCountedOccurrences)
        {
            count++;
            // A zero-width match would never advance; step past it.
            var resumeAt = next.Index + Math.Max(1, next.Length);
            if (resumeAt >= text.Length) break;
            next = regex.Match(text, resumeAt);
        }
        return count;
    }

    /// <summary>Reporting ceiling for occurrences; beyond this the exact number tells you nothing.</summary>
    public const int MaxCountedOccurrences = 100;

    /// <summary>Replace <c>{name}</c> placeholders with capture values.</summary>
    public static string Interpolate(string template, IReadOnlyDictionary<string, string> captures)
    {
        if (string.IsNullOrEmpty(template) || captures.Count == 0)
            return template;

        return Regex.Replace(template, @"\{(\w+)\}", m =>
            captures.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
