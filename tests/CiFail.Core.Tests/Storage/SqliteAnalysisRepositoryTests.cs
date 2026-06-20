using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Storage;

public class SqliteAnalysisRepositoryTests : IDisposable
{
    // A unique temp file per test instance keeps runs isolated (":memory:" would be
    // wiped when the single connection is shared, but a temp file is closest to real use).
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cifail-test-{Guid.NewGuid():N}.db");
    private readonly SqliteAnalysisRepository _repo;

    public SqliteAnalysisRepositoryTests() => _repo = new SqliteAnalysisRepository(_dbPath);

    private static AnalysisRecord Record(string ruleId, params string[] terms) => new()
    {
        AnalyzedAt = DateTimeOffset.UtcNow,
        Source = "test.log",
        Ecosystem = "dotnet",
        RuleId = ruleId,
        Matched = ruleId != "unknown",
        Fingerprint = $"{ruleId}:abc123",
        LogHash = "deadbeef",
        Excerpt = "error " + ruleId,
        Terms = terms.ToDictionary(t => t, _ => 1),
    };

    [Fact]
    public void Save_assigns_incrementing_ids_and_GetById_round_trips()
    {
        var id1 = _repo.Save(Record("nuget-nu1101", "nu1101", "package"));
        var id2 = _repo.Save(Record("csc-compile-error", "cs0103"));

        id2.Should().BeGreaterThan(id1);

        var fetched = _repo.GetById(id1);
        fetched.Should().NotBeNull();
        fetched!.RuleId.Should().Be("nuget-nu1101");
        fetched.Matched.Should().BeTrue();
        fetched.Resolution.Should().BeNull();
    }

    [Fact]
    public void GetRecent_returns_newest_first()
    {
        var first = _repo.Save(Record("a", "x"));
        var second = _repo.Save(Record("b", "y"));

        var recent = _repo.GetRecent(10);

        recent.Should().HaveCount(2);
        recent[0].Id.Should().Be(second);
        recent[1].Id.Should().Be(first);
    }

    [Fact]
    public void SetResolution_persists_note_and_returns_false_for_unknown_id()
    {
        var id = _repo.Save(Record("nuget-nu1101", "nu1101"));

        _repo.SetResolution(id, "fixed the typo").Should().BeTrue();
        _repo.SetResolution(99999, "nope").Should().BeFalse();

        var fetched = _repo.GetById(id);
        fetched!.Resolution.Should().Be("fixed the typo");
        fetched.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void LoadCorpus_round_trips_term_vectors()
    {
        _repo.Save(Record("nuget-nu1101", "nu1101", "package"));
        var corpus = _repo.LoadCorpus(10);

        corpus.Should().HaveCount(1);
        corpus[0].Terms.Should().ContainKey("nu1101").And.ContainKey("package");
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
