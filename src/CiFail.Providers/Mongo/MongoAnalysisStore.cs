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
public sealed class MongoAnalysisStore : IAnalysisStore
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
        var update = Builders<AnalysisDoc>.Update
            .Set(a => a.Resolution, note)
            .Set(a => a.ResolvedAt, DateTimeOffset.UtcNow.ToString("O"));
        var result = _analyses.UpdateOne(a => a.Id == id, update);
        return result.MatchedCount > 0;
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
