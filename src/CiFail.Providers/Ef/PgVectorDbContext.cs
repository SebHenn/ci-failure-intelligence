using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CiFail.Providers.Ef;

/// <summary>
/// PostgreSQL + pgvector model (R10): the shared schema plus an <c>embedding vector(N)</c> column
/// and a cosine index for fast nearest-neighbour search. <c>N</c> (the embedding size) must match
/// the embedder that produced the vectors. Created on first use via <c>EnsureCreated()</c>, which
/// also enables the <c>vector</c> extension declared here.
///
/// The index method (R21) is selectable: <c>hnsw</c> (default — high recall, builds well on an empty
/// table) or <c>ivfflat</c> (smaller/faster to build, but recall depends on centroids learned from
/// existing data; on the empty table <c>EnsureCreated()</c> produces, recall is poor until rebuilt).
/// </summary>
public sealed class PgVectorDbContext : AnalysisDbContext
{
    private readonly int _dimensions;
    private readonly string _indexMethod;

    public PgVectorDbContext(DbContextOptions<PgVectorDbContext> options, int dimensions, string indexMethod = "hnsw")
        : base(options)
    {
        _dimensions = dimensions;
        _indexMethod = NormalizeIndexMethod(indexMethod);
    }

    /// <summary>Only <c>hnsw</c> and <c>ivfflat</c> are supported; anything else falls back to <c>hnsw</c>.</summary>
    public static string NormalizeIndexMethod(string? method) =>
        string.Equals(method, "ivfflat", StringComparison.OrdinalIgnoreCase) ? "ivfflat" : "hnsw";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");
        base.OnModelCreating(model);
    }

    protected override void MapEmbedding(EntityTypeBuilder<AnalysisEntity> e)
    {
        e.Property(x => x.Embedding).HasColumnName("embedding").HasColumnType($"vector({_dimensions})");
        e.HasIndex(x => x.Embedding)
            .HasDatabaseName("ix_analyses_embedding")
            .HasMethod(_indexMethod)
            .HasOperators("vector_cosine_ops");
    }
}
