using System.Security.Cryptography;
using System.Text;
using CiFail.Core.Ai;
using CiFail.Core.Git;
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

    /// <summary>Below this rule confidence (the "low" bucket), AI is consulted when enabled.</summary>
    private const double LowConfidenceThreshold = 0.6;

    /// <summary>How much of the (normalized) log to hand the AI model.</summary>
    private const int AiExcerptMaxChars = 6000;

    /// <summary>How much of the (normalized) log to hand the embedding model.</summary>
    private const int EmbeddingMaxChars = 8000;

    private readonly RuleEngine _engine;
    private readonly IAnalysisStore? _store;
    private readonly IGitRepo? _git;
    private readonly IAiAnalyzer? _ai;
    private readonly IAiEmbedder? _embedder;

    public AnalysisService(
        RuleEngine engine, IAnalysisStore? store = null, IGitRepo? git = null,
        IAiAnalyzer? ai = null, IAiEmbedder? embedder = null)
    {
        _engine = engine;
        _store = store;
        _git = git;
        _ai = ai;
        _embedder = embedder;
    }

    /// <summary>Factory that loads embedded + user rule packs, with no history store.</summary>
    /// <param name="ruleDirs">Extra rule-pack directories (<c>--rules</c>), on top of the search path.</param>
    public static AnalysisService CreateDefault(IReadOnlyList<string>? ruleDirs = null) =>
        new(new RuleEngine(RulePackLoader.LoadFrom(RuleSearchPath.Resolve(ruleDirs))));

    /// <summary>Factory that also wires the given history store (similarity + persistence).</summary>
    public static AnalysisService CreateWithStore(
        IAnalysisStore store, IGitRepo? git = null, IAiAnalyzer? ai = null, IAiEmbedder? embedder = null,
        IReadOnlyList<string>? ruleDirs = null) =>
        new(new RuleEngine(RulePackLoader.LoadFrom(RuleSearchPath.Resolve(ruleDirs))), store, git, ai, embedder);

    public Models.Analysis Analyze(string source, string rawText, AnalysisOptions? options = null)
    {
        options ??= new AnalysisOptions();

        var log = LogNormalizer.Build(source, rawText);
        var ecosystem = EcosystemDetector.Detect(log, options.EcosystemOverride);
        var matches = _engine.Match(log, ecosystem);
        var rootCause = matches.Count > 0 ? matches[0] : null;
        var fingerprint = FingerprintBuilder.Build(log, rootCause);

        var aiSuggestion = MaybeSuggestWithAi(options, log, ecosystem, rootCause);
        var embedding = MaybeEmbed(log);

        IReadOnlyList<SimilarFailure> similar = Array.Empty<SimilarFailure>();
        long? historyId = null;

        if (_store is not null)
        {
            var queryTerms = Tokenizer.Tokenize(log.NormalizedText);
            similar = FindSimilar(queryTerms, embedding, options.TopSimilar);

            if (options.RecordHistory)
            {
                historyId = _store.Save(new AnalysisRecord
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
                    Embedding = embedding,
                    RepoId = _git?.RepoId,
                    GitCommit = _git?.Head,
                    GitBranch = _git?.Branch,
                    GitDirty = _git?.IsDirty ?? false,
                });
            }
        }

        return new Models.Analysis
        {
            Source = source,
            Ecosystem = ecosystem,
            Matches = matches,
            Fingerprint = fingerprint,
            HistoryId = historyId,
            SimilarFailures = similar,
            AiSuggestion = aiSuggestion,
        };
    }

    /// <summary>
    /// Consult the AI analyzer when enabled and the rules are unsure (no match, or a match below
    /// the "low" confidence bucket). Best-effort: any failure (model offline, bad response) is
    /// swallowed so the rules-only result always stands.
    /// </summary>
    private AiSuggestion? MaybeSuggestWithAi(
        AnalysisOptions options, LogDocument log, Ecosystem ecosystem, RuleMatch? rootCause)
    {
        if (!options.EnableAi || _ai is null)
            return null;
        if (rootCause is not null && rootCause.Score >= LowConfidenceThreshold)
            return null;

        try
        {
            return _ai.Suggest(new AiRequest
            {
                LogExcerpt = TailExcerpt(log.NormalizedText, AiExcerptMaxChars),
                Ecosystem = ecosystem,
                TopMatch = rootCause,
            });
        }
        catch
        {
            return null; // never fatal — same philosophy as a skipped bad rule regex
        }
    }

    /// <summary>
    /// Compute a dense embedding for the log when an embedder is wired (R10), best-effort —
    /// a model that's offline/erroring yields null and similarity falls back to TF-IDF.
    /// </summary>
    private float[]? MaybeEmbed(LogDocument log)
    {
        if (_embedder is null)
            return null;
        try
        {
            return _embedder.Embed(TailExcerpt(log.NormalizedText, EmbeddingMaxChars));
        }
        catch
        {
            return null;
        }
    }

    private static string TailExcerpt(string text, int maxChars)
    {
        text = text.Trim();
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    /// <summary>True when this service is operating inside a git repository.</summary>
    public bool HasGit => _git is not null;

    /// <summary>
    /// Auto-resolve open failures for the current repo, treating <paramref name="observedFingerprints"/>
    /// as "still happening now" (typically the fingerprints from the analyses just run, so a
    /// failure that recurred at HEAD stays open). No-op without both a store and a git context.
    /// </summary>
    public IReadOnlyList<ResolutionReconciler.Resolved> ReconcileResolutions(
        IReadOnlySet<string>? observedFingerprints = null)
    {
        if (_store is null || _git is null)
            return Array.Empty<ResolutionReconciler.Resolved>();
        return ResolutionReconciler.Reconcile(_store, _git, observedFingerprints);
    }

    private IReadOnlyList<SimilarFailure> FindSimilar(
        IReadOnlyDictionary<string, int> queryTerms, float[]? queryEmbedding, int topN)
    {
        // Scale path (R10): when the store searches vectors in the DB and we have an embedding,
        // use it instead of loading the whole corpus — that's the point of the capability.
        if (queryEmbedding is not null && _store is ISimilaritySearch vectorStore)
        {
            return vectorStore.FindSimilar(queryEmbedding, topN)
                .Select(h => new SimilarFailure
                {
                    Id = h.Meta.Id,
                    Similarity = Math.Round(h.Score, 4),
                    RuleId = h.Meta.RuleId,
                    AnalyzedAt = h.Meta.AnalyzedAt,
                    Excerpt = h.Meta.Excerpt,
                    Resolution = h.Meta.Resolution,
                })
                .ToList();
        }

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
