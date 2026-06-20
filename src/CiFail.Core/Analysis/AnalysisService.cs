using System.Security.Cryptography;
using System.Text;
using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Rules;
using CiFail.Core.Similarity;
using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Orchestrates the analyze pipeline: ingest → detect ecosystem → rule match →
/// root cause + fingerprint → similar past failures → persist. Similarity and
/// history are only active when an <see cref="IAnalysisStore"/> is supplied; the
/// pipeline is otherwise fully offline and deterministic.
/// </summary>
public sealed class AnalysisService
{
    /// <summary>How many past analyses to consider when ranking similarity.</summary>
    private const int CorpusLimit = 2000;

    private readonly RuleEngine _engine;
    private readonly IAnalysisStore? _store;

    public AnalysisService(RuleEngine engine, IAnalysisStore? store = null)
    {
        _engine = engine;
        _store = store;
    }

    /// <summary>Factory that loads embedded + user rule packs, with no history store.</summary>
    public static AnalysisService CreateDefault() =>
        new(new RuleEngine(RulePackLoader.LoadAll()));

    /// <summary>Factory that also wires the given history store (similarity + persistence).</summary>
    public static AnalysisService CreateWithStore(IAnalysisStore store) =>
        new(new RuleEngine(RulePackLoader.LoadAll()), store);

    public Models.Analysis Analyze(string source, string rawText, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();

        var log = LogNormalizer.Build(source, rawText);
        var ecosystem = EcosystemDetector.Detect(log, options.EcosystemOverride);
        var matches = _engine.Match(log, ecosystem);
        var rootCause = matches.Count > 0 ? matches[0] : null;
        var fingerprint = FingerprintBuilder.Build(log, rootCause);

        IReadOnlyList<SimilarFailure> similar = Array.Empty<SimilarFailure>();

        if (_store is not null)
        {
            var queryTerms = Tokenizer.Tokenize(log.NormalizedText);
            similar = FindSimilar(queryTerms, options.TopSimilar);

            if (options.RecordHistory)
            {
                _store.Save(new AnalysisRecord
                {
                    AnalyzedAt = DateTimeOffset.UtcNow,
                    Source = source,
                    Ecosystem = ecosystem.ToString().ToLowerInvariant(),
                    RuleId = fingerprint.RuleId,
                    Matched = rootCause is not null,
                    Fingerprint = fingerprint.ToString(),
                    LogHash = HashRaw(rawText),
                    Excerpt = BuildExcerpt(log, rootCause),
                    Terms = queryTerms,
                });
            }
        }

        return new Models.Analysis
        {
            Source = source,
            Ecosystem = ecosystem,
            Matches = matches,
            Fingerprint = fingerprint,
            SimilarFailures = similar,
        };
    }

    private IReadOnlyList<SimilarFailure> FindSimilar(
        IReadOnlyDictionary<string, int> queryTerms, int topN)
    {
        var corpus = _store!.LoadCorpus(CorpusLimit);
        if (corpus.Count == 0) return Array.Empty<SimilarFailure>();

        var ranked = TfIdfSimilarity.Rank(
            queryTerms,
            corpus.Select(c => (c.Meta, c.Terms)).ToList(),
            topN);

        return ranked.Select(r => new SimilarFailure
        {
            Id = r.Item.Id,
            Similarity = r.Score,
            RuleId = r.Item.RuleId,
            AnalyzedAt = r.Item.AnalyzedAt,
            Excerpt = r.Item.Excerpt,
            Resolution = r.Item.Resolution,
        }).ToList();
    }

    private static string BuildExcerpt(LogDocument log, RuleMatch? rootCause)
    {
        var text = rootCause?.MatchedLine ?? log.NormalizedText;
        text = text.Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private static string HashRaw(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
