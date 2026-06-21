using CiFail.Core.Analysis;
using CiFail.Core.Configuration;
using CiFail.Core.Git;
using CiFail.Core.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;

namespace CiFail.Server.Tests;

/// <summary>
/// Server-side reconciliation (R11): the client-side <see cref="ResolutionReconciler"/> runs
/// unchanged against a remote server through <see cref="HttpAnalysisStore"/> — the server only
/// exposes open failures and persists the auto-resolutions. Each test boots a server over its
/// own SQLite file so it can seed records (with git context) the HTTP API never sets itself.
/// </summary>
public sealed class ServeReconcileTests : IDisposable
{
    private const string RepoId = "root-commit";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cifail-r11-{Guid.NewGuid():N}.db");
    private WebApplication? _app;
    private string _baseUrl = null!;

    private async Task<HttpAnalysisStore> StartAsync()
    {
        _app = CiFailServer.Build(new ServeOptions
        {
            Url = "http://127.0.0.1:0",
            Database = new DatabaseConfig { Provider = "sqlite", ConnectionString = _dbPath },
        });
        await _app.StartAsync();
        _baseUrl = _app.Urls.First();
        return new HttpAnalysisStore(_baseUrl);
    }

    private long SeedFailure(string fingerprint, string commit, string? manualNote = null)
    {
        using var repo = new SqliteAnalysisRepository(_dbPath);
        var id = repo.Save(new AnalysisRecord
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
        if (manualNote is not null)
            repo.SetResolution(id, manualNote);
        return id;
    }

    [Fact]
    public async Task Reconciler_auto_resolves_an_ancestor_failure_over_http()
    {
        var id = SeedFailure("rule:p", "A");
        using var store = await StartAsync();
        var git = new FakeGit(head: "B", ancestor: ("A", "B"), subjects: new[] { "fix the thing" });

        var resolved = ResolutionReconciler.Reconcile(store, git);

        resolved.Should().ContainSingle().Which.Id.Should().Be(id);
        var read = store.GetById(id)!;
        read.Status.Should().Be(AnalysisStatus.Resolved);
        read.ResolutionSource.Should().Be(ResolutionSource.Auto);
        read.ResolvedCommit.Should().Be("B");
        read.Resolution.Should().Contain("fix the thing");
    }

    [Fact]
    public async Task Manual_resolution_is_not_overwritten_over_http()
    {
        var id = SeedFailure("rule:p", "A", manualNote: "I fixed it by hand");
        using var store = await StartAsync();
        var git = new FakeGit(head: "B", ancestor: ("A", "B"));

        var resolved = ResolutionReconciler.Reconcile(store, git);

        resolved.Should().BeEmpty(); // the row is no longer open, so it's not returned/touched
        var read = store.GetById(id)!;
        read.ResolutionSource.Should().Be(ResolutionSource.Manual);
        read.Resolution.Should().Be("I fixed it by hand");
    }

    [Fact]
    public async Task Open_failures_endpoint_is_scoped_to_the_repo()
    {
        SeedFailure("rule:p", "A");
        using var store = await StartAsync();

        store.GetOpenFailures(RepoId).Should().ContainSingle();
        store.GetOpenFailures("some-other-repo").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    /// <summary>A minimal scripted <see cref="IGitRepo"/> for the reconciler.</summary>
    private sealed class FakeGit : IGitRepo
    {
        private readonly (string ancestor, string descendant)? _ancestor;
        private readonly string[] _subjects;

        public FakeGit(string head, (string, string)? ancestor = null, string[]? subjects = null)
        {
            Head = head;
            _ancestor = ancestor;
            _subjects = subjects ?? Array.Empty<string>();
        }

        public string RepoId => ServeReconcileTests.RepoId;
        public string Head { get; }
        public string? Branch => "main";
        public bool IsDirty => false;

        public bool IsAncestor(string ancestor, string descendant) =>
            _ancestor is { } a && a.ancestor == ancestor && a.descendant == descendant;

        public IReadOnlyList<string> CommitSubjects(string fromExclusive, string toInclusive) => _subjects;
    }
}
