using System.Text.Json;
using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace CiFail.Providers.Ef;

/// <summary>
/// <see cref="IAnalysisStore"/> over EF Core, shared by all relational providers. The
/// schema is created on first use with <c>EnsureCreated()</c> (no migrations: the model
/// is append-only and tiny, so create-if-absent is enough and keeps setup zero-touch).
/// Not sealed so the pgvector store can extend it with in-DB similarity search (R10).
/// </summary>
public class EfAnalysisStore : IAnalysisStore, IFingerprintCounter, IAnalysisStats
{
    /// <summary>The EF context; exposed to derived stores (e.g. pgvector search).</summary>
    protected readonly AnalysisDbContext Db;

    public EfAnalysisStore(AnalysisDbContext db)
    {
        Db = db;
        Db.Database.EnsureCreated();
    }

    public long Save(AnalysisRecord record)
    {
        var entity = new AnalysisEntity
        {
            AnalyzedAt = record.AnalyzedAt.ToString("O"),
            Source = record.Source,
            Ecosystem = record.Ecosystem,
            RuleId = record.RuleId,
            Matched = record.Matched,
            Fingerprint = record.Fingerprint,
            LogHash = record.LogHash,
            Excerpt = record.Excerpt,
            Tokens = JsonSerializer.Serialize(record.Terms),
            // Only the pgvector model maps this column; every other provider ignores it.
            Embedding = record.Embedding is null ? null : new Vector(record.Embedding),
            RepoId = record.RepoId,
            GitCommit = record.GitCommit,
            GitBranch = record.GitBranch,
            GitDirty = record.GitDirty,
            Status = "open",
        };
        Db.Analyses.Add(entity);
        Db.SaveChanges();
        return entity.Id;
    }

    public IReadOnlyList<StoredAnalysis> GetRecent(int limit) =>
        Db.Analyses.OrderByDescending(a => a.Id).Take(Math.Max(1, limit))
            .AsEnumerable().Select(Map).ToList();

    public StoredAnalysis? GetById(long id)
    {
        var entity = Db.Analyses.AsNoTracking().FirstOrDefault(a => a.Id == id);
        return entity is null ? null : Map(entity);
    }

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max) =>
        Db.Analyses.OrderByDescending(a => a.Id).Take(Math.Max(1, max))
            .AsEnumerable()
            .Select(a => new CorpusEntry { Meta = Map(a), Terms = DeserializeTerms(a.Tokens) })
            .ToList();

    public bool SetResolution(long id, string note)
    {
        var entity = Db.Analyses.FirstOrDefault(a => a.Id == id);
        if (entity is null) return false;

        // Manual resolution always wins, even over a prior auto-resolution.
        entity.Resolution = note;
        entity.ResolvedAt = DateTimeOffset.UtcNow.ToString("O");
        entity.Status = "resolved";
        entity.ResolutionSource = "manual";
        Db.SaveChanges();
        return true;
    }

    public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) =>
        Db.Analyses.Where(a => a.RepoId == repoId && a.Status == "open")
            .OrderByDescending(a => a.Id)
            .AsEnumerable().Select(Map).ToList();

    public bool SetAutoResolution(long id, string resolvedCommit, string note)
    {
        // Only touch still-open rows; never clobber a manual (or prior auto) resolution.
        var entity = Db.Analyses.FirstOrDefault(a => a.Id == id && a.Status == "open");
        if (entity is null) return false;

        entity.Resolution = note;
        entity.ResolvedAt = DateTimeOffset.UtcNow.ToString("O");
        entity.Status = "resolved";
        entity.ResolutionSource = "auto";
        entity.ResolvedCommit = resolvedCommit;
        Db.SaveChanges();
        return true;
    }

    public int CountByFingerprint(string fingerprint) =>
        Db.Analyses.Count(a => a.Fingerprint == fingerprint);

    public StatsSnapshot ComputeStats(StatsQuery query)
    {
        // Filter by repo in SQL (so the scan budget is spent on the right rows), then let the
        // shared StatsComputer handle since-filtering + aggregation — identical to every backend.
        IQueryable<AnalysisEntity> q = Db.Analyses;
        if (query.RepoId is not null) q = q.Where(a => a.RepoId == query.RepoId);

        var rows = q.OrderByDescending(a => a.Id).Take(Math.Max(1, query.ScanLimit))
            .AsEnumerable().Select(Map).ToList();
        return StatsComputer.Compute(rows, query);
    }

    protected static StoredAnalysis Map(AnalysisEntity a) => new()
    {
        Id = a.Id,
        AnalyzedAt = DateTimeOffset.Parse(a.AnalyzedAt),
        Source = a.Source,
        Ecosystem = a.Ecosystem,
        RuleId = a.RuleId,
        Matched = a.Matched,
        Fingerprint = a.Fingerprint,
        LogHash = a.LogHash,
        Excerpt = a.Excerpt,
        Resolution = a.Resolution,
        ResolvedAt = string.IsNullOrEmpty(a.ResolvedAt) ? null : DateTimeOffset.Parse(a.ResolvedAt),
        RepoId = a.RepoId,
        GitCommit = a.GitCommit,
        GitBranch = a.GitBranch,
        GitDirty = a.GitDirty,
        Status = a.Status,
        ResolutionSource = a.ResolutionSource,
        ResolvedCommit = a.ResolvedCommit,
    };

    private static IReadOnlyDictionary<string, int> DeserializeTerms(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();

    public void Dispose() => Db.Dispose();
}
