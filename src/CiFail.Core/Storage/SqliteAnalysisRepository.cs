using System.Text.Json;
using CiFail.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace CiFail.Core.Storage;

/// <summary>
/// SQLite-backed history store. Defaults to <c>~/.cifail/history.db</c>; pass an
/// explicit path (or <c>":memory:"</c>) for tests. The schema is created on first use.
/// </summary>
public sealed class SqliteAnalysisRepository
    : IAnalysisStore, IFingerprintCounter, IAnalysisStats, IHistoryQuery, IPrunableStore, IDisposable
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
        ApplyPragmas();
        EnsureSchema();
    }

    public static string DefaultDbPath => CiFailPaths.HistoryDbPath;

    private void EnsureSchema()
    {
        using (var cmd = _connection.CreateCommand())
        {
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

        MigrateR3Columns();
    }

    /// <summary>
    /// Add the R3 git-correlation columns to pre-existing databases. New columns can only
    /// be appended in SQLite, so we add any that are missing and backfill status/source for
    /// rows resolved before R3 (a non-null resolution means a manual fix).
    /// </summary>
    private void MigrateR3Columns()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(analyses);";
            using var reader = info.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        var additions = new (string Name, string Ddl)[]
        {
            ("repo_id",           "ALTER TABLE analyses ADD COLUMN repo_id TEXT;"),
            ("git_commit",        "ALTER TABLE analyses ADD COLUMN git_commit TEXT;"),
            ("git_branch",        "ALTER TABLE analyses ADD COLUMN git_branch TEXT;"),
            ("git_dirty",         "ALTER TABLE analyses ADD COLUMN git_dirty INTEGER NOT NULL DEFAULT 0;"),
            ("status",            "ALTER TABLE analyses ADD COLUMN status TEXT NOT NULL DEFAULT 'open';"),
            ("resolution_source", "ALTER TABLE analyses ADD COLUMN resolution_source TEXT;"),
            ("resolved_commit",   "ALTER TABLE analyses ADD COLUMN resolved_commit TEXT;"),
        };

        var added = false;
        foreach (var (name, ddl) in additions)
        {
            if (existing.Contains(name)) continue;
            using var alter = _connection.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
            added = true;
        }

        if (added)
        {
            using var backfill = _connection.CreateCommand();
            backfill.CommandText = """
                UPDATE analyses
                SET status = 'resolved', resolution_source = 'manual'
                WHERE resolution IS NOT NULL;
                """;
            backfill.ExecuteNonQuery();
        }

        using var idx = _connection.CreateCommand();
        idx.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_analyses_repo_status ON analyses(repo_id, status);
            -- Every `--since` filter (stats, /metrics, the dashboard, history) sorts and ranges
            -- on this column and had no index to use.
            CREATE INDEX IF NOT EXISTS ix_analyses_analyzed_at ON analyses(analyzed_at);
            """;
        idx.ExecuteNonQuery();
    }

    /// <summary>
    /// Connection-level pragmas.
    ///
    /// <para>
    /// WAL lets readers and the writer proceed at once. Under the default rollback journal a
    /// dashboard render — which issues several separate queries — serializes against a concurrent
    /// <c>/analyze</c> writing its result, and the loser waits. <c>busy_timeout</c> turns the
    /// remaining contention into a short wait instead of an immediate <c>SQLITE_BUSY</c>.
    /// </para>
    ///
    /// <para>
    /// Both are best-effort: WAL is unavailable on some network filesystems, and failing to set a
    /// performance pragma must never stop cifail from opening its history.
    /// </para>
    /// </summary>
    private void ApplyPragmas()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Keep going on the default journal.
        }
    }

    public long Save(AnalysisRecord record)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO analyses
                (analyzed_at, source, ecosystem, rule_id, matched, fingerprint, log_hash, excerpt, tokens,
                 repo_id, git_commit, git_branch, git_dirty, status)
            VALUES
                ($at, $source, $eco, $rule, $matched, $fp, $hash, $excerpt, $tokens,
                 $repo, $commit, $branch, $dirty, 'open');
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
        cmd.Parameters.AddWithValue("$repo", (object?)record.RepoId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$commit", (object?)record.GitCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)record.GitBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dirty", record.GitDirty ? 1 : 0);

        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        _corpusCache = null; // a new row changes the corpus
        return id;
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

    /// <summary>
    /// Filtered, paged history run as SQL (R37) rather than by pulling recent rows and sifting
    /// them in memory — which is what made the dashboard's filters search only the newest 200
    /// records and silently miss everything older.
    /// </summary>
    public HistoryPage Query(HistoryQuery query)
    {
        var (where, bind) = BuildFilter(query);

        int total;
        using (var count = _connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM analyses{where};";
            bind(count);
            total = Convert.ToInt32(count.ExecuteScalar() ?? 0);
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectColumns}{where} ORDER BY id DESC LIMIT $limit OFFSET $offset;";
        bind(cmd);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

        using var reader = cmd.ExecuteReader();
        var items = new List<StoredAnalysis>();
        while (reader.Read()) items.Add(ReadStored(reader));

        // A real COUNT(*), so the total is exact and never truncated.
        return new HistoryPage(items, total);
    }

    /// <summary>
    /// Build the shared WHERE clause plus a delegate that binds its parameters, so the count and
    /// the page query cannot drift apart. Comparisons mirror <see cref="HistoryQuery.Filter"/>:
    /// exact on the enumerated columns, case-insensitive substring on the free-text search.
    /// </summary>
    private static (string Where, Action<SqliteCommand> Bind) BuildFilter(HistoryQuery q)
    {
        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        void Add(string clause, string name, object value)
        {
            clauses.Add(clause);
            parameters.Add((name, value));
        }

        if (q.Since is { } since)
            Add("analyzed_at >= $since", "$since", since.ToUniversalTime().ToString("O"));
        if (!string.IsNullOrWhiteSpace(q.RepoId))
            Add("repo_id = $repo", "$repo", q.RepoId);
        if (!string.IsNullOrWhiteSpace(q.Status))
            Add("status = $status", "$status", q.Status);
        if (!string.IsNullOrWhiteSpace(q.RuleId))
            Add("rule_id = $rule", "$rule", q.RuleId);
        if (!string.IsNullOrWhiteSpace(q.Ecosystem))
            Add("ecosystem = $eco COLLATE NOCASE", "$eco", q.Ecosystem);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // LIKE with an escaped pattern: a user searching for "100%" must not match everything.
            clauses.Add("(source LIKE $q ESCAPE '\\' OR fingerprint LIKE $q ESCAPE '\\' " +
                        "OR excerpt LIKE $q ESCAPE '\\')");
            parameters.Add(("$q", "%" + EscapeLike(q.Search!) + "%"));
        }

        var where = clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
        return (where, cmd =>
        {
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        });
    }

    /// <summary>Neutralize LIKE wildcards in user input (paired with <c>ESCAPE '\'</c>).</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Delete analyses older than the cutoff (R37). Reclaims the file with <c>VACUUM</c>, since
    /// deleting rows only frees pages inside the database — the point of pruning is usually that
    /// the file itself has grown.
    /// </summary>
    public PruneResult Prune(PruneRequest request)
    {
        var where = "analyzed_at < $before" + (request.ResolvedOnly ? " AND status = $resolved" : string.Empty);
        var cutoff = request.Before.ToUniversalTime().ToString("O");

        void Bind(SqliteCommand cmd)
        {
            cmd.Parameters.AddWithValue("$before", cutoff);
            if (request.ResolvedOnly) cmd.Parameters.AddWithValue("$resolved", AnalysisStatus.Resolved);
        }

        int affected;
        using (var count = _connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM analyses WHERE {where};";
            Bind(count);
            affected = Convert.ToInt32(count.ExecuteScalar() ?? 0);
        }

        if (request.DryRun || affected == 0)
            return new PruneResult(affected, request.DryRun);

        using (var del = _connection.CreateCommand())
        {
            del.CommandText = $"DELETE FROM analyses WHERE {where};";
            Bind(del);
            del.ExecuteNonQuery();
        }

        // The corpus cache is keyed on (count, max id); a delete can leave both unchanged while
        // the contents differ, so clear it explicitly — as Save/SetResolution already do.
        _corpusCache = null;

        try
        {
            using var vacuum = _connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Rows are gone either way; reclaiming the pages is a bonus, not the contract.
        }

        return new PruneResult(affected, DryRun: false);
    }

    // Corpus cache (R21): a single `cifail analyze` of a structured report expands into many
    // analysis units that each ask for the same similarity corpus. Reloading up to CorpusLimit
    // rows per unit is wasteful, so we cache the result keyed on a cheap (row-count, max-id, limit)
    // signature. Inserts move the signature; in-place updates (resolve) explicitly clear the cache
    // (see the write methods). So the cache stays transparent and always consistent with the DB.
    private (long Count, long MaxId, int Max)? _corpusKey;
    private IReadOnlyList<CorpusEntry>? _corpusCache;

    public IReadOnlyList<CorpusEntry> LoadCorpus(int max)
    {
        max = Math.Max(1, max);
        var (count, maxId) = CorpusSignature();
        if (_corpusCache is not null && _corpusKey == (count, maxId, max))
            return _corpusCache;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}, tokens
            FROM analyses
            ORDER BY id DESC
            LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max", max);

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

        _corpusCache = list;
        _corpusKey = (count, maxId, max);
        return list;
    }

    /// <summary>A cheap fingerprint of the table state — count plus highest id (covers inserts,
    /// deletes, and resolves-by-id) — used to decide whether the cached corpus is still valid.</summary>
    private (long Count, long MaxId) CorpusSignature()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(MAX(id), 0) FROM analyses;";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public bool SetResolution(long id, string note)
    {
        // Manual resolution always wins, even over a prior auto-resolution.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE analyses
            SET resolution = $note, resolved_at = $at,
                status = 'resolved', resolution_source = 'manual'
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) _corpusCache = null; // a resolve updates a row in place — invalidate the cache
        return ok;
    }

    public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE repo_id = $repo AND status = 'open' ORDER BY id DESC;";
        cmd.Parameters.AddWithValue("$repo", repoId);

        using var reader = cmd.ExecuteReader();
        var list = new List<StoredAnalysis>();
        while (reader.Read()) list.Add(ReadStored(reader));
        return list;
    }

    public bool SetAutoResolution(long id, string resolvedCommit, string note)
    {
        // Only touch still-open rows; never clobber a manual (or prior auto) resolution.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE analyses
            SET resolution = $note, resolved_at = $at,
                status = 'resolved', resolution_source = 'auto', resolved_commit = $commit
            WHERE id = $id AND status = 'open';
            """;
        cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$commit", resolvedCommit);
        cmd.Parameters.AddWithValue("$id", id);
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) _corpusCache = null; // a resolve updates a row in place — invalidate the cache
        return ok;
    }

    public StatsSnapshot ComputeStats(StatsQuery query)
    {
        // Push the since/repo filters into SQL (ISO-8601 strings sort chronologically), then
        // let the shared StatsComputer do the aggregation so every backend matches exactly.
        using var cmd = _connection.CreateCommand();
        var where = new List<string>();
        if (query.Since is not null)
        {
            where.Add("analyzed_at >= $since");
            cmd.Parameters.AddWithValue("$since", query.Since.Value.ToUniversalTime().ToString("O"));
        }
        if (query.RepoId is not null)
        {
            where.Add("repo_id = $repo");
            cmd.Parameters.AddWithValue("$repo", query.RepoId);
        }

        var clause = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"{SelectColumns}{clause} ORDER BY id DESC LIMIT $scan;";
        cmd.Parameters.AddWithValue("$scan", Math.Max(1, query.ScanLimit));

        using var reader = cmd.ExecuteReader();
        var rows = new List<StoredAnalysis>();
        while (reader.Read()) rows.Add(ReadStored(reader));

        // Filters are already applied in SQL; computer re-applies them harmlessly.
        return StatsComputer.Compute(rows, query);
    }

    public int CountByFingerprint(string fingerprint)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM analyses WHERE fingerprint = $fp;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private const string Columns =
        "id, analyzed_at, source, ecosystem, rule_id, matched, fingerprint, log_hash, excerpt, " +
        "resolution, resolved_at, repo_id, git_commit, git_branch, git_dirty, status, " +
        "resolution_source, resolved_commit";

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
        RepoId = r.IsDBNull(r.GetOrdinal("repo_id")) ? null : r.GetString(r.GetOrdinal("repo_id")),
        GitCommit = r.IsDBNull(r.GetOrdinal("git_commit")) ? null : r.GetString(r.GetOrdinal("git_commit")),
        GitBranch = r.IsDBNull(r.GetOrdinal("git_branch")) ? null : r.GetString(r.GetOrdinal("git_branch")),
        GitDirty = !r.IsDBNull(r.GetOrdinal("git_dirty")) && r.GetInt32(r.GetOrdinal("git_dirty")) != 0,
        Status = r.GetString(r.GetOrdinal("status")),
        ResolutionSource = r.IsDBNull(r.GetOrdinal("resolution_source")) ? null : r.GetString(r.GetOrdinal("resolution_source")),
        ResolvedCommit = r.IsDBNull(r.GetOrdinal("resolved_commit")) ? null : r.GetString(r.GetOrdinal("resolved_commit")),
    };

    public void Dispose() => _connection.Dispose();
}
