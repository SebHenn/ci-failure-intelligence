using CiFail.Core.Similarity;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace CiFail.Providers.Ef;

/// <summary>
/// The EF store plus in-database vector similarity (R10): inherits all the normal CRUD from
/// <see cref="EfAnalysisStore"/> and adds <see cref="ISimilaritySearch"/> backed by pgvector's
/// HNSW cosine index. Used only by the <c>pgvector</c> provider.
/// </summary>
public sealed class PgVectorAnalysisStore : EfAnalysisStore, ISimilaritySearch
{
    public PgVectorAnalysisStore(PgVectorDbContext db) : base(db) { }

    public IReadOnlyList<SimilarityHit> FindSimilar(float[] queryEmbedding, int topN)
    {
        var query = new Vector(queryEmbedding);

        // Ordered by cosine distance via the HNSW index; score = 1 - distance (1 == identical).
        return Db.Analyses
            .Where(a => a.Embedding != null)
            .Select(a => new { Entity = a, Distance = a.Embedding!.CosineDistance(query) })
            .OrderBy(x => x.Distance)
            .Take(Math.Max(1, topN))
            .AsEnumerable()
            .Select(x => new SimilarityHit(Map(x.Entity), 1.0 - x.Distance))
            .ToList();
    }
}
