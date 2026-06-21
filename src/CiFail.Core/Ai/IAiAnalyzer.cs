using CiFail.Core.Models;

namespace CiFail.Core.Ai;

/// <summary>
/// An optional, pluggable AI backend that suggests a root cause + fix when the rule engine
/// is unsure. Implementations must be best-effort: never throw for ordinary failures (model
/// offline, bad response) — return null and let the rules-only result stand.
/// </summary>
public interface IAiAnalyzer
{
    /// <summary>Suggest a root cause + fix, or null if unavailable.</summary>
    AiSuggestion? Suggest(AiRequest request);
}
