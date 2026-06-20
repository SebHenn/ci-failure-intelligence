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
}
