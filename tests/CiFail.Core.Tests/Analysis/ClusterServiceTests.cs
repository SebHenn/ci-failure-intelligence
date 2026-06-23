using CiFail.Core.Analysis;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

/// <summary>
/// ClusterService routes to a store's own <see cref="IClusterer"/> when present, else falls back to
/// loading the corpus and clustering in-app — mirroring StatsService (R25).
/// </summary>
public class ClusterServiceTests
{
    [Fact]
    public void Uses_the_store_clusterer_when_available()
    {
        var store = new CapableStore();

        var result = ClusterService.Compute(store, new ClusterQuery { Threshold = 0.5 });

        store.ClusterCalls.Should().Be(1);
        store.LoadCorpusCalls.Should().Be(0);
        result.TotalAnalyzed.Should().Be(CapableStore.Marker);
    }

    [Fact]
    public void Falls_back_to_loading_the_corpus_when_store_cannot_cluster()
    {
        var store = new PlainStore();

        var result = ClusterService.Compute(store, new ClusterQuery { Threshold = 0.5, IncludeSingletons = true });

        store.LoadCorpusCalls.Should().Be(1);
        result.TotalAnalyzed.Should().Be(2);
        result.Clusters.Should().ContainSingle().Which.Count.Should().Be(2);
    }

    [Fact]
    public void Json_round_trips()
    {
        var original = new ClusterResult
        {
            TotalAnalyzed = 5,
            Clusters = new[]
            {
                new FailureCluster
                {
                    Label = "npm-404",
                    Count = 3,
                    MemberIds = new long[] { 9, 5, 1 },
                    Ecosystems = new[] { "node" },
                    LastSeen = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero),
                },
            },
        };

        var dto = ClustersJson.ToDto(original);
        var back = ClustersJson.FromDto(dto);

        back.Should().BeEquivalentTo(original);
    }

    private static CorpusEntry Entry(long id, params string[] terms) => new()
    {
        Meta = new StoredAnalysis
        {
            Id = id, AnalyzedAt = DateTimeOffset.UnixEpoch.AddMinutes(id), Source = "s",
            Ecosystem = "node", RuleId = "r", Matched = true, Fingerprint = $"r:{id}",
            LogHash = "h", Excerpt = "x",
        },
        Terms = terms.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
    };

    private sealed class PlainStore : IAnalysisStore
    {
        public int LoadCorpusCalls { get; private set; }

        public IReadOnlyList<CorpusEntry> LoadCorpus(int max)
        {
            LoadCorpusCalls++;
            return new[] { Entry(1, "boom", "crash", "stack"), Entry(2, "boom", "crash", "stack") };
        }

        public long Save(AnalysisRecord record) => 1;
        public IReadOnlyList<StoredAnalysis> GetRecent(int limit) => Array.Empty<StoredAnalysis>();
        public StoredAnalysis? GetById(long id) => null;
        public bool SetResolution(long id, string note) => false;
        public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => Array.Empty<StoredAnalysis>();
        public bool SetAutoResolution(long id, string c, string n) => false;
        public void Dispose() { }
    }

    private sealed class CapableStore : IAnalysisStore, IClusterer
    {
        public const int Marker = 42;
        public int ClusterCalls { get; private set; }
        public int LoadCorpusCalls { get; private set; }

        public ClusterResult ComputeClusters(ClusterQuery query)
        {
            ClusterCalls++;
            return new ClusterResult { Clusters = Array.Empty<FailureCluster>(), TotalAnalyzed = Marker };
        }

        public IReadOnlyList<CorpusEntry> LoadCorpus(int max) { LoadCorpusCalls++; return Array.Empty<CorpusEntry>(); }
        public long Save(AnalysisRecord record) => 1;
        public IReadOnlyList<StoredAnalysis> GetRecent(int limit) => Array.Empty<StoredAnalysis>();
        public StoredAnalysis? GetById(long id) => null;
        public bool SetResolution(long id, string note) => false;
        public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => Array.Empty<StoredAnalysis>();
        public bool SetAutoResolution(long id, string c, string n) => false;
        public void Dispose() { }
    }
}
