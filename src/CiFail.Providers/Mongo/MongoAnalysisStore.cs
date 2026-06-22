using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CiFail.Providers.Mongo;

/// <summary>
/// <see cref="IAnalysisStore"/> backed by MongoDB (document per analysis). MongoDB has
/// no auto-increment, so we mint sequential long ids from a <c>counters</c> document —
/// keeping the same <c>long</c> id contract the relational/SQLite stores expose (so
/// <c>cifail resolve &lt;id&gt;</c> works identically). Term vectors are stored natively
/// as a sub-document rather than a JSON string.
/// </summary>
public sealed class MongoAnalysisStore : IAnalysisStore, IFingerprintCounter, IAnalysisStats
{
    private readonly IMongoClient _client;
    private readonly IMongoCollection<AnalysisDoc> _analyses;
    private readonly IMongoCollection<Counter> _counters;

    public MongoAnalysisStore(string connectionString)
    {
        var url = new MongoUrl(connectionString);
        _client = new MongoClient(url);
        var db = _client.GetDatabase(string.IsNullOrWhiteSpace(url.DatabaseName) ? "cifail" : url.DatabaseName);
        _analyses = db.GetCollection<AnalysisDoc>("analyses");
        _counters = db.GetCollection<Counter>("counters");
        _analyses.Indexes.CreateOne(new CreateIndexModel<AnalysisDoc>(
            Builders<AnalysisDoc>.IndexKeys.Ascending(a => a.Fingerprint)));
    }

    public long Save(AnalysisRecord record)
    {
        var doc = new AnalysisDoc
        {
            Id = NextId(),
            AnalyzedAt = record.AnalyzedAt.ToString("O"),
            Source = record.Source,
            Ecosystem = record.Ecosystem,
            RuleId = record.RuleId,
            Matched = record.Matched,
            Fingerprint = record.Fingerprint,
            LogHash = record.LogHash,
            Excerpt = record.Excerpt,
            Terms = new Dictionary<string, int>(record.Terms),
            RepoId = record.RepoId,
            GitCommit = record.GitCommit,
            GitBranch = record.GitBranch,
            GitDirty = record.GitDirty,
            Status = "open",
        };
        _analyses.InsertOne(doc);
        return doc.Id;
    }

    public IReadOnlyList<StoredAnalysis> GetRecent(int limit) =>
        _analyses.Find(FilterDefinition<AnalysisDoc>.Empty)
            .SortByDescending(a => a.Id).Limit(Math.Max(1, limit))
            .ToList().Select(Map).ToList();

    public StoredAnalysis? GetById(long id)
    {
        var doc = _analyses.Find(a => a.Id == id).FirstOrDefault();
        return doc is null ? null : Map(doc);
    }

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max) =>
        _analyses.Find(FilterDefinition<AnalysisDoc>.Empty)
            .SortByDescending(a => a.Id).Limit(Math.Max(1, max))
            .ToList()
            .Select(a => new CorpusEntry { Meta = Map(a), Terms = a.Terms })
            .ToList();

    public bool SetResolution(long id, string note)
    {
        // Manual resolution always wins, even over a prior auto-resolution.
        var update = Builders<AnalysisDoc>.Update
            .Set(a => a.Resolution, note)
            .Set(a => a.ResolvedAt, DateTimeOffset.UtcNow.ToString("O"))
            .Set(a => a.Status, "resolved")
            .Set(a => a.ResolutionSource, "manual");
        var result = _analyses.UpdateOne(a => a.Id == id, update);
        return result.MatchedCount > 0;
    }

    public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) =>
        _analyses.Find(a => a.RepoId == repoId && a.Status == "open")
            .SortByDescending(a => a.Id)
            .ToList().Select(Map).ToList();

    public bool SetAutoResolution(long id, string resolvedCommit, string note)
    {
        // Only touch still-open rows; never clobber a manual (or prior auto) resolution.
        var update = Builders<AnalysisDoc>.Update
            .Set(a => a.Resolution, note)
            .Set(a => a.ResolvedAt, DateTimeOffset.UtcNow.ToString("O"))
            .Set(a => a.Status, "resolved")
            .Set(a => a.ResolutionSource, "auto")
            .Set(a => a.ResolvedCommit, resolvedCommit);
        var result = _analyses.UpdateOne(a => a.Id == id && a.Status == "open", update);
        return result.ModifiedCount > 0;
    }

    public int CountByFingerprint(string fingerprint) =>
        (int)_analyses.CountDocuments(a => a.Fingerprint == fingerprint);

    public StatsSnapshot ComputeStats(StatsQuery query)
    {
        // Filter by repo in the query, then let the shared StatsComputer aggregate (since +
        // flaky + MTTR), so Mongo matches every other backend exactly.
        var filter = query.RepoId is null
            ? FilterDefinition<AnalysisDoc>.Empty
            : Builders<AnalysisDoc>.Filter.Eq(a => a.RepoId, query.RepoId);

        var rows = _analyses.Find(filter)
            .SortByDescending(a => a.Id).Limit(Math.Max(1, query.ScanLimit))
            .ToList().Select(Map).ToList();
        return StatsComputer.Compute(rows, query);
    }

    private long NextId()
    {
        var update = Builders<Counter>.Update.Inc(c => c.Seq, 1L);
        var options = new FindOneAndUpdateOptions<Counter> { IsUpsert = true, ReturnDocument = ReturnDocument.After };
        var counter = _counters.FindOneAndUpdate(c => c.Id == "analyses", update, options);
        return counter.Seq;
    }

    private static StoredAnalysis Map(AnalysisDoc a) => new()
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
        Status = string.IsNullOrEmpty(a.Status) ? "open" : a.Status,
        ResolutionSource = a.ResolutionSource,
        ResolvedCommit = a.ResolvedCommit,
    };

    public void Dispose() { /* MongoClient is process-shared and self-managing; nothing to release. */ }
}

[BsonIgnoreExtraElements]
internal sealed class AnalysisDoc
{
    [BsonId] public long Id { get; set; }
    public string AnalyzedAt { get; set; } = "";
    public string Source { get; set; } = "";
    public string Ecosystem { get; set; } = "";
    public string RuleId { get; set; } = "";
    public bool Matched { get; set; }
    public string Fingerprint { get; set; } = "";
    public string LogHash { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public Dictionary<string, int> Terms { get; set; } = new();
    public string? Resolution { get; set; }
    public string? ResolvedAt { get; set; }
    public string? RepoId { get; set; }
    public string? GitCommit { get; set; }
    public string? GitBranch { get; set; }
    public bool GitDirty { get; set; }
    public string Status { get; set; } = "open";
    public string? ResolutionSource { get; set; }
    public string? ResolvedCommit { get; set; }
}

[BsonIgnoreExtraElements]
internal sealed class Counter
{
    [BsonId] public string Id { get; set; } = "";
    public long Seq { get; set; }
}

/// <summary>MongoDB provider (document store via MongoDB.Driver).</summary>
public sealed class MongoStoreProvider : IStoreProvider
{
    public string Name => "mongodb";
    public string Description => "MongoDB — shared/team history (document store).";

    public IAnalysisStore Create(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "the 'mongodb' database provider needs a connection string " +
                "(set it via --db-connection, CIFAIL_DB_CONNECTION, or config.yaml).");
        return new MongoAnalysisStore(connectionString);
    }
}
