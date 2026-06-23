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

    /// <summary>Map a 0..1 confidence to the plain word cifail shows users.</summary>
    public static string Confidence(double confidence) => confidence switch
    {
        >= 0.8 => "high",
        >= 0.6 => "medium",
        _ => "low",
    };
}
