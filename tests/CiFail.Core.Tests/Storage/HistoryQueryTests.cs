using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Storage;

/// <summary>
/// R37: filtering and paging history in the store instead of in memory.
///
/// <para>
/// Every consumer used to fetch the newest N rows and sift them with LINQ. On the dashboard N was
/// 200, so "show me resolved failures" could only find one among the most recent 200 records —
/// an older one was simply unreachable, and the page said "No failures match" as though it had
/// looked. That is a wrong answer, not a slow one, which is why these tests deliberately use a
/// corpus larger than any scan window.
/// </para>
/// </summary>
public class HistoryQueryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"cifail-histq-{Guid.NewGuid():N}.db");
    private readonly SqliteAnalysisRepository _store;

    public HistoryQueryTests()
    {
        _store = new SqliteAnalysisRepository(_dbPath);
        Seed();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }

    private const int Total = 400;

    /// <summary>
    /// One old resolved failure, then enough newer noise to bury it well past a 200-row window.
    /// </summary>
    private void Seed()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var needleId = _store.Save(Record(baseTime, "needle.log", "node", "npm-404", "npm-404:needle"));
        _store.SetResolution(needleId, "bumped the registry token");

        for (var i = 0; i < Total; i++)
        {
            _store.Save(Record(
                baseTime.AddMinutes(i + 1),
                $"noise-{i}.log",
                i % 2 == 0 ? "dotnet" : "go",
                "generic-nonzero-exit",
                $"generic-nonzero-exit:{i:D4}"));
        }
    }

    private static AnalysisRecord Record(
        DateTimeOffset at, string source, string ecosystem, string ruleId, string fingerprint) => new()
    {
        AnalyzedAt = at,
        Source = source,
        Ecosystem = ecosystem,
        RuleId = ruleId,
        Matched = true,
        Fingerprint = fingerprint,
        LogHash = fingerprint,
        Excerpt = $"something went wrong in {source}",
        Terms = new Dictionary<string, int> { ["error"] = 1 },
    };

    /// <summary>The exact bug: the one resolved row is older than 400 newer records.</summary>
    [Fact]
    public void A_resolved_failure_far_outside_the_recent_window_is_still_found()
    {
        var page = HistoryService.Query(_store, new HistoryQuery
        {
            Status = AnalysisStatus.Resolved,
            Limit = 50,
        });

        page.Total.Should().Be(1);
        page.Items.Should().ContainSingle()
            .Which.Source.Should().Be("needle.log");
    }

    [Fact]
    public void Ecosystem_filter_counts_every_matching_row_not_just_recent_ones()
    {
        var page = HistoryService.Query(_store, new HistoryQuery { Ecosystem = "dotnet", Limit = 10 });

        page.Total.Should().Be(Total / 2);
        page.Items.Should().HaveCount(10, "the page is capped even though the total is not");
        page.Items.Should().OnlyContain(r => r.Ecosystem == "dotnet");
    }

    [Fact]
    public void Paging_walks_the_whole_result_set_without_repeating_a_row()
    {
        var seen = new List<long>();
        for (var offset = 0; offset < Total + 1; offset += 100)
        {
            var page = HistoryService.Query(_store, new HistoryQuery { Limit = 100, Offset = offset });
            seen.AddRange(page.Items.Select(i => i.Id));
        }

        seen.Should().HaveCount(Total + 1);
        seen.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Newest_comes_first()
    {
        var page = HistoryService.Query(_store, new HistoryQuery { Limit = 5 });

        page.Items.Select(i => i.Id).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Search_matches_source_and_excerpt()
    {
        HistoryService.Query(_store, new HistoryQuery { Search = "needle" })
            .Total.Should().Be(1);

        HistoryService.Query(_store, new HistoryQuery { Search = "something went wrong" })
            .Total.Should().Be(Total + 1);
    }

    /// <summary>
    /// A search for a literal <c>%</c> must not behave like a wildcard — the SQL path builds a
    /// LIKE pattern, and unescaped user input there matches everything.
    /// </summary>
    [Fact]
    public void Search_treats_like_wildcards_as_literal_text()
    {
        HistoryService.Query(_store, new HistoryQuery { Search = "%" }).Total.Should().Be(0);
        HistoryService.Query(_store, new HistoryQuery { Search = "_" }).Total.Should().Be(0);
    }

    [Fact]
    public void Since_excludes_older_rows()
    {
        var cutoff = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        var page = HistoryService.Query(_store, new HistoryQuery { Since = cutoff, Limit = 1000 });

        page.Items.Should().OnlyContain(r => r.AnalyzedAt >= cutoff);
        page.Total.Should().BeLessThan(Total + 1);
    }

    [Fact]
    public void Filters_combine()
    {
        var page = HistoryService.Query(_store, new HistoryQuery
        {
            Ecosystem = "node",
            Status = AnalysisStatus.Resolved,
        });

        page.Total.Should().Be(1);
        page.Items.Single().Source.Should().Be("needle.log");
    }

    /// <summary>
    /// The SQL path and the in-memory fallback must agree, or a user gets different answers from
    /// SQLite than from a store that hasn't implemented IHistoryQuery.
    /// </summary>
    [Theory]
    [InlineData("dotnet", null, null)]
    [InlineData(null, "resolved", null)]
    [InlineData(null, null, "needle")]
    [InlineData("go", null, "noise")]
    public void The_sql_path_and_the_fallback_agree(string? eco, string? status, string? search)
    {
        var query = new HistoryQuery { Ecosystem = eco, Status = status, Search = search, Limit = 25 };

        var viaSql = HistoryService.Query(_store, query);
        var viaFallback = HistoryService.Query(new FallbackOnlyStore(_store), query);

        viaFallback.Items.Select(i => i.Id).Should().Equal(viaSql.Items.Select(i => i.Id));
        viaFallback.Total.Should().Be(viaSql.Total);
    }

    /// <summary>
    /// Wraps the real store but hides <see cref="IHistoryQuery"/>, so HistoryService takes the
    /// GetRecent + filter-in-memory path that Mongo and the EF engines use.
    /// </summary>
    private sealed class FallbackOnlyStore : IAnalysisStore
    {
        private readonly IAnalysisStore _inner;
        public FallbackOnlyStore(IAnalysisStore inner) => _inner = inner;

        public long Save(AnalysisRecord record) => _inner.Save(record);
        public IReadOnlyList<StoredAnalysis> GetRecent(int limit) => _inner.GetRecent(limit);
        public StoredAnalysis? GetById(long id) => _inner.GetById(id);
        public IReadOnlyList<CorpusEntry> LoadCorpus(int max) => _inner.LoadCorpus(max);
        public bool SetResolution(long id, string note) => _inner.SetResolution(id, note);
        public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => _inner.GetOpenFailures(repoId);
        public bool SetAutoResolution(long id, string commit, string note) =>
            _inner.SetAutoResolution(id, commit, note);
        public void Dispose() { /* the test owns the inner store */ }
    }
}
