using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

/// <summary>Deterministic single-link clustering over term bags (R25).</summary>
public class FailureClustererTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CorpusEntry Entry(
        long id, IReadOnlyDictionary<string, int> terms, DateTimeOffset? at = null,
        string ecosystem = "node", string ruleId = "r", bool matched = true, string? repoId = null) =>
        new()
        {
            Meta = new StoredAnalysis
            {
                Id = id,
                AnalyzedAt = at ?? T0.AddMinutes(id),
                Source = "s",
                Ecosystem = ecosystem,
                RuleId = ruleId,
                Matched = matched,
                Fingerprint = $"{ruleId}:{id}",
                LogHash = "h",
                Excerpt = "x",
                RepoId = repoId,
            },
            Terms = terms,
        };

    private static Dictionary<string, int> Bag(params string[] terms) =>
        terms.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    [Fact]
    public void Groups_near_identical_failures_and_separates_a_distinct_one()
    {
        var corpus = new[]
        {
            Entry(1, Bag("cannot", "find", "module", "leftpad")),
            Entry(2, Bag("cannot", "find", "module", "leftpad", "again")),
            Entry(3, Bag("cannot", "find", "module", "leftpad")),
            Entry(4, Bag("segmentation", "fault", "core", "dumped")),
        };

        var result = FailureClusterer.Compute(corpus, new ClusterQuery { Threshold = 0.5, IncludeSingletons = true });

        result.TotalAnalyzed.Should().Be(4);
        result.Clusters.Should().HaveCount(2);

        var big = result.Clusters[0];
        big.Count.Should().Be(3);
        big.MemberIds.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        big.MemberIds.Should().BeInDescendingOrder();
    }

    [Fact]
    public void Singletons_are_excluded_by_default()
    {
        var corpus = new[]
        {
            Entry(1, Bag("alpha", "beta", "gamma")),
            Entry(2, Bag("alpha", "beta", "gamma")),
            Entry(3, Bag("delta", "epsilon", "zeta")), // unique
        };

        var result = FailureClusterer.Compute(corpus, new ClusterQuery { Threshold = 0.5 });

        result.Clusters.Should().HaveCount(1);
        result.Clusters[0].Count.Should().Be(2);
    }

    [Fact]
    public void Single_link_transitivity_chains_A_B_C_into_one_cluster()
    {
        // A~B (share x,y) and B~C (share y,z) but A and C share only y — single-link still merges all.
        var corpus = new[]
        {
            Entry(1, Bag("x", "x", "y")),
            Entry(2, Bag("x", "y", "z")),
            Entry(3, Bag("y", "z", "z")),
        };

        var result = FailureClusterer.Compute(corpus, new ClusterQuery { Threshold = 0.3, IncludeSingletons = true });

        result.Clusters.Should().HaveCount(1);
        result.Clusters[0].Count.Should().Be(3);
    }

    [Fact]
    public void A_high_threshold_keeps_loosely_related_failures_apart()
    {
        var corpus = new[]
        {
            Entry(1, Bag("a", "b", "c", "d")),
            Entry(2, Bag("a", "e", "f", "g")), // shares only "a"
        };

        var result = FailureClusterer.Compute(corpus, new ClusterQuery { Threshold = 0.9, IncludeSingletons = true });

        result.Clusters.Should().HaveCount(2, "neither pair clears a 0.9 bar");
    }

    [Fact]
    public void Label_is_the_dominant_matched_rule_id()
    {
        var corpus = new[]
        {
            Entry(1, Bag("oom", "killed", "exit", "137"), ruleId: "generic-oom"),
            Entry(2, Bag("oom", "killed", "exit", "137"), ruleId: "generic-oom"),
            Entry(3, Bag("oom", "killed", "exit", "137"), ruleId: "other", matched: false),
        };

        var result = FailureClusterer.Compute(corpus, new ClusterQuery { Threshold = 0.5 });

        result.Clusters.Should().ContainSingle();
        result.Clusters[0].Label.Should().Be("generic-oom");
    }

    [Fact]
    public void Filters_by_since_and_repo()
    {
        var corpus = new[]
        {
            Entry(1, Bag("a", "b", "c"), at: T0, repoId: "repoA"),
            Entry(2, Bag("a", "b", "c"), at: T0.AddDays(10), repoId: "repoA"),
            Entry(3, Bag("a", "b", "c"), at: T0.AddDays(10), repoId: "repoB"),
        };

        var bySince = FailureClusterer.Compute(corpus,
            new ClusterQuery { Threshold = 0.5, Since = T0.AddDays(5), IncludeSingletons = true });
        bySince.TotalAnalyzed.Should().Be(2);

        var byRepo = FailureClusterer.Compute(corpus,
            new ClusterQuery { Threshold = 0.5, RepoId = "repoA", IncludeSingletons = true });
        byRepo.TotalAnalyzed.Should().Be(2);
    }

    [Fact]
    public void Empty_corpus_yields_no_clusters()
    {
        var result = FailureClusterer.Compute(Array.Empty<CorpusEntry>(), new ClusterQuery());

        result.TotalAnalyzed.Should().Be(0);
        result.Clusters.Should().BeEmpty();
    }
}
