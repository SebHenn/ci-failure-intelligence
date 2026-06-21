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
    }

    private static AnalysisRecord SampleRecord(string ruleId, params (string term, int count)[] terms) => new()
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
    };

    private static void Saves_and_reads_back_by_id(IAnalysisStore store)
    {
        var id = store.Save(SampleRecord("nuget-nu1101", ("nu1101", 2), ("package", 1)));
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
        store.Save(SampleRecord("corpus", ("alpha", 3), ("beta", 5)));

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
}
