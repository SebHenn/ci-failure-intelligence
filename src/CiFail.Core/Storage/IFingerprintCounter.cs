namespace CiFail.Core.Storage;

/// <summary>
/// Optional capability (like <see cref="Similarity.ISimilaritySearch"/>) for telling whether a
/// fingerprint has been seen before — used by notifications (R13) to distinguish a brand-new
/// failure from a recurrence. Stores that don't implement it simply don't get that distinction.
/// </summary>
public interface IFingerprintCounter
{
    /// <summary>How many stored analyses carry this fingerprint (including any just saved).</summary>
    int CountByFingerprint(string fingerprint);
}
