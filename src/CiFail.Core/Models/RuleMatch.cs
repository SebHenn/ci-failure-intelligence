namespace CiFail.Core.Models;

/// <summary>
/// The result of a <see cref="RuleDefinition"/> matching a log: the rule, the
/// captured named groups, the line that matched (with its position and surrounding
/// context), and the rule's confidence.
/// </summary>
public sealed class RuleMatch
{
    public required RuleDefinition Rule { get; init; }

    /// <summary>Named capture groups from the rule's regex (name -> value).</summary>
    public required IReadOnlyDictionary<string, string> Captures { get; init; }

    /// <summary>The normalized line that matched.</summary>
    public required string MatchedLine { get; init; }

    /// <summary>Score for ranking; defaults to the rule's declared confidence.</summary>
    public required double Score { get; init; }

    /// <summary>The fix text with capture placeholders interpolated.</summary>
    public required string Fix { get; init; }

    /// <summary>
    /// 1-based line number of <see cref="MatchedLine"/> in the normalized log, or 0 when unknown.
    ///
    /// <para>
    /// Nothing tracked this before, which is why SARIF results could only ever point at the top of
    /// a file: a finding without a line is one the reader has to go and locate themselves.
    /// </para>
    /// </summary>
    public int LineNumber { get; init; }

    /// <summary>Normalized log lines immediately before <see cref="MatchedLine"/>, in order.</summary>
    public IReadOnlyList<string> ContextBefore { get; init; } = Array.Empty<string>();

    /// <summary>Normalized log lines immediately after <see cref="MatchedLine"/>, in order.</summary>
    public IReadOnlyList<string> ContextAfter { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How many times this rule matched the log (at least 1).
    ///
    /// <para>
    /// The engine reported only the first occurrence, so twelve TypeScript errors and one looked
    /// exactly alike in the output. Counting them is also the honest input to any ranking that
    /// wants to weigh corroboration.
    /// </para>
    /// </summary>
    public int OccurrenceCount { get; init; } = 1;

    /// <summary>The matched line together with its surrounding context, oldest first.</summary>
    public IEnumerable<string> ContextBlock =>
        ContextBefore.Append(MatchedLine).Concat(ContextAfter);

    /// <summary>
    /// 1-based line number of the first line of <see cref="ContextBlock"/>, so a renderer can
    /// number the block without recomputing it.
    /// </summary>
    public int ContextStartLine => Math.Max(1, LineNumber - ContextBefore.Count);
}
