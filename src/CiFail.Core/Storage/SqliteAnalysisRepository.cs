using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CiFail.Core.Storage;

/// <summary>
/// SQLite-backed history store. Defaults to <c>~/.cifail/history.db</c>; pass an
/// explicit path (or <c>":memory:"</c>) for tests. The schema is created on first use.
/// </summary>
public sealed class SqliteAnalysisRepository : IAnalysisStore, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteAnalysisRepository(string? dbPath = null)
    {
        dbPath ??= DefaultDbPath;

        if (!string.Equals(dbPath, ":memory:", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        // Pooling=False so the file handle is released promptly on Dispose (the
        // repository owns a single long-lived connection; pooling only causes the
        // file to stay locked after close).
        _connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _connection.Open();
        EnsureSchema();
    }

    public static string DefaultDbPath => CiFailPaths.HistoryDbPath;

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS analyses (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                analyzed_at  TEXT    NOT NULL,
                source       TEXT    NOT NULL,
                ecosystem    TEXT    NOT NULL,
                rule_id      TEXT    NOT NULL,
                matched      INTEGER NOT NULL,
                fingerprint  TEXT    NOT NULL,
                log_hash     TEXT    NOT NULL,
                excerpt      TEXT    NOT NULL,
                tokens       TEXT    NOT NULL,
                resolution   TEXT,
                resolved_at  TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_analyses_fingerprint ON analyses(fingerprint);
            """;
        cmd.ExecuteNonQuery();
    }

    public long Save(AnalysisRecord record)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO analyses
                (analyzed_at, source, ecosystem, rule_id, matched, fingerprint, log_hash, excerpt, tokens)
            VALUES
                ($at, $source, $eco, $rule, $matched, $fp, $hash, $excerpt, $tokens);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$at", record.AnalyzedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$source", record.Source);
        cmd.Parameters.AddWithValue("$eco", record.Ecosystem);
        cmd.Parameters.AddWithValue("$rule", record.RuleId);
        cmd.Parameters.AddWithValue("$matched", record.Matched ? 1 : 0);
        cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
        cmd.Parameters.AddWithValue("$hash", record.LogHash);
        cmd.Parameters.AddWithValue("$excerpt", record.Excerpt);
        cmd.Parameters.AddWithValue("$tokens", JsonSerializer.Serialize(record.Terms));

        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<StoredAnalysis> GetRecent(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectColumns} ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using var reader = cmd.ExecuteReader();
        var list = new List<StoredAnalysis>();
        while (reader.Read()) list.Add(ReadStored(reader));
        return list;
    }

    public StoredAnalysis? GetById(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadStored(reader) : null;
    }

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}, tokens
            FROM analyses
            ORDER BY id DESC
            LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max", Math.Max(1, max));

        using var reader = cmd.ExecuteReader();
        var list = new List<CorpusEntry>();
        while (reader.Read())
        {
            var meta = ReadStored(reader);
            var tokensJson = reader.GetString(reader.GetOrdinal("tokens"));
            var terms = JsonSerializer.Deserialize<Dictionary<string, int>>(tokensJson)
                        ?? new Dictionary<string, int>();
            list.Add(new CorpusEntry { Meta = meta, Terms = terms });
        }
        return list;
    }

    public bool SetResolution(long id, string note)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE analyses
            SET resolution = $note, resolved_at = $at
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private const string Columns =
        "id, analyzed_at, source, ecosystem, rule_id, matched, fingerprint, log_hash, excerpt, resolution, resolved_at";

    private static readonly string SelectColumns = $"SELECT {Columns} FROM analyses";

    private static StoredAnalysis ReadStored(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        AnalyzedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("analyzed_at"))),
        Source = r.GetString(r.GetOrdinal("source")),
        Ecosystem = r.GetString(r.GetOrdinal("ecosystem")),
        RuleId = r.GetString(r.GetOrdinal("rule_id")),
        Matched = r.GetInt32(r.GetOrdinal("matched")) != 0,
        Fingerprint = r.GetString(r.GetOrdinal("fingerprint")),
        LogHash = r.GetString(r.GetOrdinal("log_hash")),
        Excerpt = r.GetString(r.GetOrdinal("excerpt")),
        Resolution = r.IsDBNull(r.GetOrdinal("resolution")) ? null : r.GetString(r.GetOrdinal("resolution")),
        ResolvedAt = r.IsDBNull(r.GetOrdinal("resolved_at"))
            ? null
            : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("resolved_at"))),
    };

    public void Dispose() => _connection.Dispose();
}
