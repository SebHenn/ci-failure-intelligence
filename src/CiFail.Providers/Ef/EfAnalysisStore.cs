using System.Text.Json;
using CiFail.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace CiFail.Providers.Ef;

/// <summary>
/// <see cref="IAnalysisStore"/> over EF Core, shared by all relational providers. The
/// schema is created on first use with <c>EnsureCreated()</c> (no migrations: the model
/// is append-only and tiny, so create-if-absent is enough and keeps setup zero-touch).
/// </summary>
public sealed class EfAnalysisStore : IAnalysisStore
{
    private readonly CiFailDbContext _db;

    public EfAnalysisStore(CiFailDbContext db)
    {
        _db = db;
        _db.Database.EnsureCreated();
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
            RepoId = record.RepoId,
            GitCommit = record.GitCommit,
            GitBranch = record.GitBranch,
            GitDirty = record.GitDirty,
            Status = "open",
        };
        _db.Analyses.Add(entity);
        _db.SaveChanges();
        return entity.Id;
    }

    public IReadOnlyList<StoredAnalysis> GetRecent(int limit) =>
        _db.Analyses.OrderByDescending(a => a.Id).Take(Math.Max(1, limit))
            .AsEnumerable().Select(Map).ToList();

    public StoredAnalysis? GetById(long id)
    {
        var entity = _db.Analyses.AsNoTracking().FirstOrDefault(a => a.Id == id);
        return entity is null ? null : Map(entity);
    }

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max) =>
        _db.Analyses.OrderByDescending(a => a.Id).Take(Math.Max(1, max))
            .AsEnumerable()
            .Select(a => new CorpusEntry { Meta = Map(a), Terms = DeserializeTerms(a.Tokens) })
            .ToList();

    public bool SetResolution(long id, string note)
    {
        var entity = _db.Analyses.FirstOrDefault(a => a.Id == id);
        if (entity is null) return false;

        // Manual resolution always wins, even over a prior auto-resolution.
        entity.Resolution = note;
        entity.ResolvedAt = DateTimeOffset.UtcNow.ToString("O");
        entity.Status = "resolved";
        entity.ResolutionSource = "manual";
        _db.SaveChanges();
        return true;
    }

    public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) =>
        _db.Analyses.Where(a => a.RepoId == repoId && a.Status == "open")
            .OrderByDescending(a => a.Id)
            .AsEnumerable().Select(Map).ToList();

    public bool SetAutoResolution(long id, string resolvedCommit, string note)
    {
        // Only touch still-open rows; never clobber a manual (or prior auto) resolution.
        var entity = _db.Analyses.FirstOrDefault(a => a.Id == id && a.Status == "open");
        if (entity is null) return false;

        entity.Resolution = note;
        entity.ResolvedAt = DateTimeOffset.UtcNow.ToString("O");
        entity.Status = "resolved";
        entity.ResolutionSource = "auto";
        entity.ResolvedCommit = resolvedCommit;
        _db.SaveChanges();
        return true;
    }

    private static StoredAnalysis Map(AnalysisEntity a) => new()
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

    public void Dispose() => _db.Dispose();
}
