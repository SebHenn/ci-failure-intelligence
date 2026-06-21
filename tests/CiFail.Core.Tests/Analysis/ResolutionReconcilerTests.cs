using CiFail.Core.Analysis;
using CiFail.Core.Git;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

public class ResolutionReconcilerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cifail-recon-{Guid.NewGuid():N}.db");
    private readonly SqliteAnalysisRepository _repo;
    private const string RepoId = "root-commit";

    public ResolutionReconcilerTests() => _repo = new SqliteAnalysisRepository(_dbPath);

    private long SaveFailure(string fingerprint, string commit) => _repo.Save(new AnalysisRecord
    {
        AnalyzedAt = DateTimeOffset.UtcNow,
        Source = "build.log",
        Ecosystem = "dotnet",
        RuleId = "r",
        Matched = true,
        Fingerprint = fingerprint,
        LogHash = "h",
        Excerpt = "e",
        Terms = new Dictionary<string, int>(),
        RepoId = RepoId,
        GitCommit = commit,
        GitBranch = "main",
    });

    [Fact]
    public void Failure_at_ancestor_commit_is_auto_resolved_when_not_observed_at_head()
    {
        var id = SaveFailure("rule:p", "A");
        var git = new FakeGit(head: "B", ancestors: new() { ("A", "B") })
            .WithSubjects("A", "B", "fix the thing");

        var resolved = ResolutionReconciler.Reconcile(_repo, git);

        resolved.Should().ContainSingle().Which.Id.Should().Be(id);
        var read = _repo.GetById(id)!;
        read.Status.Should().Be(AnalysisStatus.Resolved);
        read.ResolutionSource.Should().Be(ResolutionSource.Auto);
        read.ResolvedCommit.Should().Be("B");
        read.Resolution.Should().Contain("fix the thing");
    }

    [Fact]
    public void Failure_still_observed_at_head_stays_open()
    {
        var id = SaveFailure("rule:p", "A");
        var git = new FakeGit(head: "B", ancestors: new() { ("A", "B") });

        var observed = new HashSet<string> { "rule:p" };
        var resolved = ResolutionReconciler.Reconcile(_repo, git, observed);

        resolved.Should().BeEmpty();
        _repo.GetById(id)!.Status.Should().Be(AnalysisStatus.Open);
    }

    [Fact]
    public void Failure_recorded_at_current_head_is_not_resolved()
    {
        var id = SaveFailure("rule:p", "B");
        var git = new FakeGit(head: "B", ancestors: new());

        ResolutionReconciler.Reconcile(_repo, git).Should().BeEmpty();
        _repo.GetById(id)!.Status.Should().Be(AnalysisStatus.Open);
    }

    [Fact]
    public void Failure_on_an_unrelated_history_line_is_left_open()
    {
        var id = SaveFailure("rule:p", "X"); // X is not an ancestor of B
        var git = new FakeGit(head: "B", ancestors: new());

        ResolutionReconciler.Reconcile(_repo, git).Should().BeEmpty();
        _repo.GetById(id)!.Status.Should().Be(AnalysisStatus.Open);
    }

    [Fact]
    public void Other_repos_open_failures_are_untouched()
    {
        var otherRepo = _repo.Save(new AnalysisRecord
        {
            AnalyzedAt = DateTimeOffset.UtcNow, Source = "s", Ecosystem = "dotnet", RuleId = "r",
            Matched = true, Fingerprint = "rule:p", LogHash = "h", Excerpt = "e",
            Terms = new Dictionary<string, int>(), RepoId = "different-repo", GitCommit = "A",
        });
        var git = new FakeGit(head: "B", ancestors: new() { ("A", "B") });

        ResolutionReconciler.Reconcile(_repo, git).Should().BeEmpty();
        _repo.GetById(otherRepo)!.Status.Should().Be(AnalysisStatus.Open);
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>A scripted <see cref="IGitRepo"/> for deterministic reconciler tests.</summary>
    private sealed class FakeGit : IGitRepo
    {
        private readonly HashSet<(string, string)> _ancestors;
        private readonly Dictionary<(string, string), List<string>> _subjects = new();

        public FakeGit(string head, HashSet<(string ancestor, string descendant)> ancestors)
        {
            Head = head;
            _ancestors = ancestors;
        }

        public string RepoId => ResolutionReconcilerTests.RepoId;
        public string Head { get; }
        public string? Branch => "main";
        public bool IsDirty => false;

        public bool IsAncestor(string ancestor, string descendant) =>
            _ancestors.Contains((ancestor, descendant));

        public FakeGit WithSubjects(string from, string to, params string[] subjects)
        {
            _subjects[(from, to)] = subjects.ToList();
            return this;
        }

        public IReadOnlyList<string> CommitSubjects(string fromExclusive, string toInclusive) =>
            _subjects.TryGetValue((fromExclusive, toInclusive), out var s) ? s : Array.Empty<string>();
    }
}
