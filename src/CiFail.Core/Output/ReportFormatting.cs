namespace CiFail.Core.Output;

/// <summary>
/// Shared confidence mappings for the report renderers (R24), so SARIF and Markdown never disagree
/// on how a confidence score is labelled.
/// </summary>
public static class ReportFormatting
{
    /// <summary>Map a 0..1 confidence to a SARIF result level: error / warning / note.</summary>
    public static string SarifLevel(double confidence) => confidence switch
    {
        >= 0.8 => "error",
        >= 0.6 => "warning",
        _ => "note",
    };

    /// <summary>
    /// The SARIF level for a match, preferring the rule's declared <c>severity</c> (R36) over the
    /// confidence buckets.
    ///
    /// <para>
    /// Confidence answers "how sure are we this is what happened"; severity answers "how bad is
    /// it". Deriving the level from confidence alone meant a confidently-identified cosmetic
    /// warning was reported as an error and a tentatively-identified fatal one as a note.
    /// Rules without a severity keep the old behaviour exactly.
    /// </para>
    /// </summary>
    public static string SarifLevel(double confidence, string? severity) =>
        string.IsNullOrWhiteSpace(severity) ? SarifLevel(confidence) : severity.Trim().ToLowerInvariant();

    /// <summary>Map a 0..1 confidence to the plain word cifail shows users.</summary>
    public static string Confidence(double confidence) => confidence switch
    {
        >= 0.8 => "high",
        >= 0.6 => "medium",
        _ => "low",
    };
}
