using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Rules;

namespace CiFail.Core.Analysis;

/// <summary>
/// Orchestrates the analyze pipeline: ingest → detect ecosystem → rule match →
/// root cause + fingerprint. Memory (similarity/history) and optional AI are layered
/// in later milestones; this M1 service is intentionally offline and deterministic.
/// </summary>
public sealed class AnalysisService
{
    private readonly RuleEngine _engine;

    public AnalysisService(RuleEngine engine) => _engine = engine;

    /// <summary>Convenience factory that loads embedded + user rule packs.</summary>
    public static AnalysisService CreateDefault() =>
        new(new RuleEngine(RulePackLoader.LoadAll()));

    public Models.Analysis Analyze(string source, string rawText, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();

        var log = LogNormalizer.Build(source, rawText);
        var ecosystem = EcosystemDetector.Detect(log, options.EcosystemOverride);
        var matches = _engine.Match(log, ecosystem);
        var fingerprint = FingerprintBuilder.Build(log, matches.Count > 0 ? matches[0] : null);

        return new Models.Analysis
        {
            Source = source,
            Ecosystem = ecosystem,
            Matches = matches,
            Fingerprint = fingerprint,
        };
    }
}
