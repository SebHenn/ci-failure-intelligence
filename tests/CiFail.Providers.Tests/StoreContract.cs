using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using FluentAssertions;

namespace CiFail.Providers.Tests;

/// <summary>
/// The behavioural contract every <see cref="IAnalysisStore"/> implementation must honour,
/// regardless of backend. Each provider's test class points this at a freshly-provisioned
/// store so SQLite-via-EF (local), Postgres, MySQL, SQL Server and MongoDB are all held to
/// the exact same round-trip guarantees.
/// </summary>
public static class StoreContract
{
    public static void Verify(IAnalysisStore store)
    {
        Saves_and_reads_back_by_id(store);
        Recent_is_newest_first(store);
        Corpus_round_trips_term_vectors(store);
        Resolution_is_recorded(store);
        Resolving_unknown_id_returns_false(store);
        Missing_id_returns_null(store);
        Git_fields_round_trip(store);
        Open_failures_are_scoped_to_their_repo(store);
        Auto_resolution_records_source_and_commit(store);
        Manual_resolution_overrides_and_wins(store);
        Stats_reflect_saved_failures(store);
    }

    private static AnalysisRecord SampleRecord(
        string ruleId, string? repoId = null, string? commit = null,
        params (string term, int count)[] terms) => new()
    {
        AnalyzedAt = DateTimeOffset.UtcNow,
        Source = "build.log",
        Ecosystem = "dotnet",
        RuleId = ruleId,
        Matched = true,
        Fingerprint = $"{ruleId}:abc123",
        LogHash = "deadbeef",
        Excerpt = "error NU1101: Unable to find package",
        Terms = terms.ToDictionary(t => t.term, t => t.count),
        RepoId = repoId,
        GitCommit = commit,
        GitBranch = repoId is null ? null : "main",
        GitDirty = false,
    };

    private static void Saves_and_reads_back_by_id(IAnalysisStore store)
    {
        var id = store.Save(SampleRecord("nuget-nu1101", terms: new[] { ("nu1101", 2), ("package", 1) }));
        id.Should().BeGreaterThan(0);

        var read = store.GetById(id);
        read.Should().NotBeNull();
        read!.RuleId.Should().Be("nuget-nu1101");
        read.Matched.Should().BeTrue();
        read.Fingerprint.Should().Be("nuget-nu1101:abc123");
        read.Excerpt.Should().Contain("NU1101");
        read.Resolution.Should().BeNull();
        read.ResolvedAt.Should().BeNull();
    }

    private static void Recent_is_newest_first(IAnalysisStore store)
    {
        var first = store.Save(SampleRecord("first"));
        var second = store.Save(SampleRecord("second"));

        var recent = store.GetRecent(10);
        recent.Should().HaveCountGreaterThanOrEqualTo(2);
        // The two ids we just wrote must appear with the later one ahead of the earlier.
        var ids = recent.Select(r => r.Id).ToList();
        ids.IndexOf(second).Should().BeLessThan(ids.IndexOf(first));
    }

    private static void Corpus_round_trips_term_vectors(IAnalysisStore store)
    {
        store.Save(SampleRecord("corpus", terms: new[] { ("alpha", 3), ("beta", 5) }));

        var corpus = store.LoadCorpus(100);
        var entry = corpus.First(e => e.Meta.RuleId == "corpus");
        entry.Terms.Should().ContainKey("alpha").WhoseValue.Should().Be(3);
        entry.Terms.Should().ContainKey("beta").WhoseValue.Should().Be(5);
    }

    private static void Resolution_is_recorded(IAnalysisStore store)
    {
        var id = store.Save(SampleRecord("to-resolve"));

        store.SetResolution(id, "bumped the package version").Should().BeTrue();

        var read = store.GetById(id);
        read!.Resolution.Should().Be("bumped the package version");
        read.ResolvedAt.Should().NotBeNull();
    }

    private static void Resolving_unknown_id_returns_false(IAnalysisStore store)
        => store.SetResolution(999_999, "nope").Should().BeFalse();

    private static void Missing_id_returns_null(IAnalysisStore store)
        => store.GetById(888_888).Should().BeNull();

    private static void Git_fields_round_trip(IAnalysisStore store)
    {
        var id = store.Save(SampleRecord("git-fields", repoId: "repo-A", commit: "c0ffee"));
        var read = store.GetById(id)!;
        read.RepoId.Should().Be("repo-A");
        read.GitCommit.Should().Be("c0ffee");
        read.GitBranch.Should().Be("main");
        read.GitDirty.Should().BeFalse();
        read.Status.Should().Be(AnalysisStatus.Open);
        read.ResolutionSource.Should().BeNull();
    }

    private static void Open_failures_are_scoped_to_their_repo(IAnalysisStore store)
    {
        var repo = $"repo-{Guid.NewGuid():N}";
        var mine = store.Save(SampleRecord("open-1", repoId: repo, commit: "aaa"));
        store.Save(SampleRecord("open-2", repoId: $"other-{Guid.NewGuid():N}", commit: "bbb"));

        var open = store.GetOpenFailures(repo);
        open.Should().OnlyContain(f => f.RepoId == repo && f.Status == AnalysisStatus.Open);
        open.Select(f => f.Id).Should().Contain(mine);
    }

    private static void Auto_resolution_records_source_and_commit(IAnalysisStore store)
    {
        var repo = $"repo-{Guid.NewGuid():N}";
        var id = store.Save(SampleRecord("auto", repoId: repo, commit: "old"));

        store.SetAutoResolution(id, "fixsha", "likely fixed by fixsha").Should().BeTrue();

        var read = store.GetById(id)!;
        read.Status.Should().Be(AnalysisStatus.Resolved);
        read.ResolutionSource.Should().Be(ResolutionSource.Auto);
        read.ResolvedCommit.Should().Be("fixsha");
        read.Resolution.Should().Contain("fixsha");
        store.GetOpenFailures(repo).Select(f => f.Id).Should().NotContain(id);

        // A second auto-resolution is a no-op (already resolved).
        store.SetAutoResolution(id, "another", "nope").Should().BeFalse();
    }

    private static void Stats_reflect_saved_failures(IAnalysisStore store)
    {
        // Scope to a fresh repo so the assertions are deterministic regardless of prior data.
        // Goes through StatsService so it covers both the store's IAnalysisStats (SQLite/EF/Mongo)
        // and the in-app fallback identically.
        var repo = $"repo-{Guid.NewGuid():N}";
        var recurring = store.Save(SampleRecord("recur", repoId: repo, commit: "a"));
        store.Save(SampleRecord("recur", repoId: repo, commit: "b"));   // same fingerprint -> recurrence
        store.Save(SampleRecord("single", repoId: repo, commit: "c"));
        store.SetResolution(recurring, "fixed").Should().BeTrue();

        var stats = StatsService.Compute(store, new StatsQuery { RepoId = repo, Top = 10 });

        stats.Total.Should().Be(3);
        stats.Resolved.Should().Be(1);
        stats.Open.Should().Be(2);
        stats.ByEcosystem.Should().ContainSingle(c => c.Key == "dotnet").Which.Count.Should().Be(3);
        stats.TopFailures.Should().Contain(f => f.Fingerprint == "recur:abc123" && f.Count == 2);
        stats.RecurrenceRate.Should().BeApproximately(0.5, 0.001); // 1 of 2 distinct fingerprints recurred
    }

    private static void Manual_resolution_overrides_and_wins(IAnalysisStore store)
    {
        var repo = $"repo-{Guid.NewGuid():N}";
        var id = store.Save(SampleRecord("manual", repoId: repo, commit: "old"));

        // Auto first, then a manual resolution must override it.
        store.SetAutoResolution(id, "autosha", "auto note").Should().BeTrue();
        store.SetResolution(id, "fixed by hand").Should().BeTrue();

        var read = store.GetById(id)!;
        read.Status.Should().Be(AnalysisStatus.Resolved);
        read.ResolutionSource.Should().Be(ResolutionSource.Manual);
        read.Resolution.Should().Be("fixed by hand");
    }
}
