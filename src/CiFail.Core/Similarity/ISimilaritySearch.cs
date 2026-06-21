using CiFail.Core.Storage;

namespace CiFail.Core.Similarity;

/// <summary>
/// An optional capability a store may implement (in addition to <see cref="IAnalysisStore"/>)
/// to do nearest-neighbour similarity search <em>in the database</em> over stored embeddings —
/// e.g. PostgreSQL + pgvector (R10). Stores that don't implement it keep the default in-app
/// TF-IDF path (<see cref="Analysis.AnalysisService"/> picks this up when present and an
/// embedding is available). Keeping it a side interface means SQLite and the offline CLI are
/// untouched.
/// </summary>
public interface ISimilaritySearch
{
    /// <summary>
    /// Return up to <paramref name="topN"/> stored analyses nearest to <paramref name="queryEmbedding"/>,
    /// most similar first. <see cref="SimilarityHit.Score"/> is a 0..1 similarity (1 = identical).
    /// </summary>
    IReadOnlyList<SimilarityHit> FindSimilar(float[] queryEmbedding, int topN);
}

/// <summary>One nearest-neighbour result: the stored record plus its similarity score.</summary>
public sealed record SimilarityHit(StoredAnalysis Meta, double Score);
