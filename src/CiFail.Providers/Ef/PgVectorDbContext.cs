using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CiFail.Providers.Ef;

/// <summary>
/// PostgreSQL + pgvector model (R10): the shared schema plus an <c>embedding vector(N)</c> column
/// and an HNSW cosine index for fast nearest-neighbour search. <c>N</c> (the embedding size) must
/// match the embedder that produced the vectors. Created on first use via <c>EnsureCreated()</c>,
/// which also enables the <c>vector</c> extension declared here.
/// </summary>
public sealed class PgVectorDbContext : AnalysisDbContext
{
    private readonly int _dimensions;

    public PgVectorDbContext(DbContextOptions<PgVectorDbContext> options, int dimensions)
        : base(options) => _dimensions = dimensions;

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
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops");
    }
}
