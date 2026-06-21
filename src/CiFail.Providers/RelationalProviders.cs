using CiFail.Core.Configuration;
using CiFail.Core.Storage;
using CiFail.Providers.Ef;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace CiFail.Providers;

/// <summary>Shared helpers for the EF Core-backed relational providers.</summary>
internal static class RelationalSupport
{
    public static string Require(string provider, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                $"the '{provider}' database provider needs a connection string " +
                "(set it via --db-connection, CIFAIL_DB_CONNECTION, or config.yaml).");
        return connectionString;
    }

    public static EfAnalysisStore Open(DbContextOptions<CiFailDbContext> options) =>
        new(new CiFailDbContext(options));
}

/// <summary>PostgreSQL provider (EF Core + Npgsql).</summary>
public sealed class PostgresStoreProvider : IStoreProvider
{
    public string Name => "postgres";
    public string Description => "PostgreSQL — shared/team history (EF Core + Npgsql).";

    public IAnalysisStore Create(string? connectionString)
    {
        var cs = RelationalSupport.Require(Name, connectionString);
        var options = new DbContextOptionsBuilder<CiFailDbContext>().UseNpgsql(cs).Options;
        return RelationalSupport.Open(options);
    }
}

/// <summary>MySQL / MariaDB provider (EF Core + Pomelo).</summary>
public sealed class MySqlStoreProvider : IStoreProvider
{
    public string Name => "mysql";
    public string Description => "MySQL / MariaDB — shared/team history (EF Core + Pomelo).";

    public IAnalysisStore Create(string? connectionString)
    {
        var cs = RelationalSupport.Require(Name, connectionString);
        // AutoDetect opens a brief connection to learn the server version; acceptable
        // since Create() is only called when we're about to use the database anyway.
        var options = new DbContextOptionsBuilder<CiFailDbContext>()
            .UseMySql(cs, ServerVersion.AutoDetect(cs)).Options;
        return RelationalSupport.Open(options);
    }
}

/// <summary>
/// PostgreSQL + pgvector provider (R10): same relational history as <see cref="PostgresStoreProvider"/>
/// plus an embedding column and HNSW index for in-database similarity search. The vector size comes
/// from <c>CIFAIL_AI_EMBED_DIM</c> (default 768, matching Ollama <c>nomic-embed-text</c>) and must
/// match the embedder that produced the vectors.
/// </summary>
public sealed class PgVectorStoreProvider : IStoreProvider
{
    public string Name => "pgvector";
    public string Description => "PostgreSQL + pgvector — shared history with vector similarity (R10).";

    public IAnalysisStore Create(string? connectionString)
    {
        var cs = RelationalSupport.Require(Name, connectionString);
        var options = new DbContextOptionsBuilder<PgVectorDbContext>()
            .UseNpgsql(cs, o => o.UseVector()).Options;
        return new PgVectorAnalysisStore(new PgVectorDbContext(options, ResolveDimensions()));
    }

    private static int ResolveDimensions()
    {
        var v = Environment.GetEnvironmentVariable(ConfigLoader.AiEmbeddingDimsEnvVar);
        return int.TryParse(v, out var d) && d > 0 ? d : ConfigLoader.DefaultEmbeddingDimensions;
    }
}

/// <summary>Microsoft SQL Server provider (EF Core + Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerStoreProvider : IStoreProvider
{
    public string Name => "sqlserver";
    public string Description => "Microsoft SQL Server — shared/team history (EF Core).";

    public IAnalysisStore Create(string? connectionString)
    {
        var cs = RelationalSupport.Require(Name, connectionString);
        var options = new DbContextOptionsBuilder<CiFailDbContext>().UseSqlServer(cs).Options;
        return RelationalSupport.Open(options);
    }
}
