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

    private static AnalysisRecord GitRecord(string ruleId, string repoId, string commit) => Record(ruleId) with
    {
        RepoId = repoId,
        GitCommit = commit,
        GitBranch = "main",
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
        fetched.Status.Should().Be(AnalysisStatus.Resolved);
        fetched.ResolutionSource.Should().Be(ResolutionSource.Manual);
    }

    [Fact]
    public void GetOpenFailures_is_scoped_to_repo_and_excludes_resolved()
    {
        var openId = _repo.Save(GitRecord("a", "repo-1", "c1"));
        var resolvedId = _repo.Save(GitRecord("b", "repo-1", "c2"));
        _repo.Save(GitRecord("c", "repo-2", "c3")); // different repo

        _repo.SetResolution(resolvedId, "done");

        var open = _repo.GetOpenFailures("repo-1");
        open.Select(f => f.Id).Should().ContainSingle().Which.Should().Be(openId);
    }

    [Fact]
    public void SetAutoResolution_marks_auto_and_does_not_overwrite_a_resolved_row()
    {
        var id = _repo.Save(GitRecord("a", "repo-1", "old"));

        _repo.SetAutoResolution(id, "fixsha", "likely fixed by fixsha").Should().BeTrue();
        var fetched = _repo.GetById(id)!;
        fetched.Status.Should().Be(AnalysisStatus.Resolved);
        fetched.ResolutionSource.Should().Be(ResolutionSource.Auto);
        fetched.ResolvedCommit.Should().Be("fixsha");

        // Already resolved → second auto call is a no-op.
        _repo.SetAutoResolution(id, "other", "nope").Should().BeFalse();
    }

    [Fact]
    public void Manual_resolution_overrides_a_prior_auto_resolution()
    {
        var id = _repo.Save(GitRecord("a", "repo-1", "old"));
        _repo.SetAutoResolution(id, "autosha", "auto note");

        _repo.SetResolution(id, "actually fixed by hand").Should().BeTrue();

        var fetched = _repo.GetById(id)!;
        fetched.ResolutionSource.Should().Be(ResolutionSource.Manual);
        fetched.Resolution.Should().Be("actually fixed by hand");
    }

    [Fact]
    public void CountByFingerprint_counts_only_matching_rows()
    {
        // Two runs share a fingerprint (a recurring failure); a third is distinct.
        _repo.Save(Record("nuget-nu1101", "nu1101"));
        _repo.Save(Record("nuget-nu1101", "nu1101"));
        _repo.Save(Record("csc-compile-error", "cs0103"));

        _repo.CountByFingerprint("nuget-nu1101:abc123").Should().Be(2);
        _repo.CountByFingerprint("csc-compile-error:abc123").Should().Be(1);
        _repo.CountByFingerprint("never-seen:000").Should().Be(0);
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
