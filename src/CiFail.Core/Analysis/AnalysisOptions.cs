namespace CiFail.Core.Analysis;

/// <summary>Knobs for a single analyze run, mirroring the CLI flags.</summary>
public sealed class AnalysisOptions
{
    /// <summary>Force a specific ecosystem instead of auto-detecting (CLI: --type).</summary>
    public string? EcosystemOverride { get; init; }

    /// <summary>Consult the optional AI analyzer on low rule confidence (CLI: --ai).</summary>
    public bool EnableAi { get; init; }

    /// <summary>Persist this analysis to history (CLI: inverse of --no-history).</summary>
    public bool RecordHistory { get; init; } = true;

    /// <summary>Maximum number of similar past failures to return (CLI: --top).</summary>
    public int TopSimilar { get; init; } = 3;

    /// <summary>
    /// Log lines to keep either side of a matched line (CLI: --context). Zero shows the matched
    /// line alone, which is what every release before this one did.
    /// </summary>
    public int ContextLines { get; init; } = DefaultContextLines;

    /// <summary>
    /// Enough to show what a compiler printed under the error line without the panel becoming
    /// the log itself.
    /// </summary>
    public const int DefaultContextLines = 3;
}
