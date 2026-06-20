namespace CiFail.Core.Models;

/// <summary>
/// A compact identity for a failure, used for storage and grouping of recurrences.
/// Built from the winning rule id (when present) plus a hash of the key normalized
/// tokens, so the "same" failure fingerprints consistently across runs.
/// </summary>
public sealed class FailureFingerprint
{
    /// <summary>Winning rule id, or "unknown" when nothing matched.</summary>
    public required string RuleId { get; init; }

    /// <summary>Stable hex hash derived from the normalized signature line(s).</summary>
    public required string Hash { get; init; }

    public override string ToString() => $"{RuleId}:{Hash}";
}
