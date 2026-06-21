using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CiFail.Providers.Ef;

/// <summary>
/// Shared EF Core model base for every relational provider. Column/table/index names mirror
/// the embedded SQLite schema so history is recognizable across backends. Schema is created on
/// first use via <c>Database.EnsureCreated()</c> (no migrations — see <see cref="EfAnalysisStore"/>).
/// The only thing that varies per backend is how the optional <see cref="AnalysisEntity.Embedding"/>
/// is mapped (only pgvector has a vector type), so that step is a virtual hook.
/// </summary>
public abstract class AnalysisDbContext : DbContext
{
    protected AnalysisDbContext(DbContextOptions options) : base(options) { }

    public DbSet<AnalysisEntity> Analyses => Set<AnalysisEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var e = model.Entity<AnalysisEntity>();
        e.ToTable("analyses");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        e.Property(x => x.AnalyzedAt).HasColumnName("analyzed_at").IsRequired();
        e.Property(x => x.Source).HasColumnName("source").IsRequired();
        e.Property(x => x.Ecosystem).HasColumnName("ecosystem").IsRequired();
        e.Property(x => x.RuleId).HasColumnName("rule_id").IsRequired();
        e.Property(x => x.Matched).HasColumnName("matched").IsRequired();
        e.Property(x => x.Fingerprint).HasColumnName("fingerprint").IsRequired();
        e.Property(x => x.LogHash).HasColumnName("log_hash").IsRequired();
        e.Property(x => x.Excerpt).HasColumnName("excerpt").IsRequired();
        e.Property(x => x.Tokens).HasColumnName("tokens").IsRequired();
        e.Property(x => x.Resolution).HasColumnName("resolution");
        e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        e.Property(x => x.RepoId).HasColumnName("repo_id");
        e.Property(x => x.GitCommit).HasColumnName("git_commit");
        e.Property(x => x.GitBranch).HasColumnName("git_branch");
        e.Property(x => x.GitDirty).HasColumnName("git_dirty").IsRequired();
        e.Property(x => x.Status).HasColumnName("status").IsRequired();
        e.Property(x => x.ResolutionSource).HasColumnName("resolution_source");
        e.Property(x => x.ResolvedCommit).HasColumnName("resolved_commit");
        e.HasIndex(x => x.Fingerprint).HasDatabaseName("ix_analyses_fingerprint");
        e.HasIndex(x => new { x.RepoId, x.Status }).HasDatabaseName("ix_analyses_repo_status");
        MapEmbedding(e);
    }

    /// <summary>Map the optional embedding column. Default: ignore it (no vector type here).</summary>
    protected virtual void MapEmbedding(EntityTypeBuilder<AnalysisEntity> e) =>
        e.Ignore(x => x.Embedding);
}

/// <summary>
/// The concrete model used by the Postgres / MySQL / SQL Server providers — no vector column.
/// </summary>
public sealed class CiFailDbContext : AnalysisDbContext
{
    public CiFailDbContext(DbContextOptions<CiFailDbContext> options) : base(options) { }
}
