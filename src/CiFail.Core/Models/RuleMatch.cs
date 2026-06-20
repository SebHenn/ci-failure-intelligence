namespace CiFail.Core.Models;

/// <summary>
/// The result of a <see cref="RuleDefinition"/> matching a log: the rule, the
/// captured named groups, the line that matched, and the rule's confidence.
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
}
