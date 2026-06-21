using CiFail.Core.Ai;
using CiFail.Core.Analysis;
using CiFail.Core.Rules;
using CiFail.Core.Similarity;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Similarity;

/// <summary>
/// R10 wiring in <see cref="AnalysisService"/>: when an embedder is present AND the store does
/// vector search, similarity is served from the store; otherwise it falls back to in-app TF-IDF.
/// The embedding is best-effort and, when produced, is persisted on the saved record.
/// </summary>
public class AnalysisServiceSimilarityTests
{
    private const string Log = "error: something went wrong while building the widget module";

    private static AnalysisService Service(IAnalysisStore store, IAiEmbedder? embedder) =>
        new(new RuleEngine(RulePackLoader.LoadAll()), store, git: null, ai: null, embedder: embedder);

    [Fact]
    public void Uses_vector_search_when_embedder_and_capable_store_are_present()
    {
        var store = new FakeVectorStore();
        var analysis = Service(store, new FakeEmbedder()).Analyze("s", Log);

        store.VectorSearchCalls.Should().Be(1);
        store.LoadCorpusCalls.Should().Be(0); // did NOT fall back to TF-IDF
        analysis.SimilarFailures.Should().ContainSingle().Which.Id.Should().Be(FakeVectorStore.HitId);
    }

    [Fact]
    public void Persists_the_embedding_on_the_saved_record()
    {
        var store = new FakeVectorStore();
        Service(store, new FakeEmbedder()).Analyze("s", Log);

        store.LastSaved!.Embedding.Should().Equal(FakeEmbedder.Vector);
    }

    [Fact]
    public void Falls_back_to_tfidf_when_no_embedder()
    {
        var store = new FakeVectorStore();
        Service(store, embedder: null).Analyze("s", Log);

        store.VectorSearchCalls.Should().Be(0);
        store.LoadCorpusCalls.Should().Be(1);
        store.LastSaved!.Embedding.Should().BeNull();
    }

    [Fact]
    public void Falls_back_to_tfidf_when_store_is_not_vector_capable()
    {
        var store = new FakePlainStore();
        Service(store, new FakeEmbedder()).Analyze("s", Log);

        store.LoadCorpusCalls.Should().Be(1);
        store.LastSaved!.Embedding.Should().Equal(FakeEmbedder.Vector); // still recorded for later
    }

    [Fact]
    public void Embedder_failure_is_swallowed_and_falls_back_to_tfidf()
    {
        var store = new FakeVectorStore();
        var analysis = Service(store, new ThrowingEmbedder()).Analyze("s", Log);

        analysis.Should().NotBeNull();
        store.VectorSearchCalls.Should().Be(0);     // no embedding => no vector search
        store.LoadCorpusCalls.Should().Be(1);
        store.LastSaved!.Embedding.Should().BeNull();
    }

    // ---- fakes --------------------------------------------------------------

    private sealed class FakeEmbedder : IAiEmbedder
    {
        public static readonly float[] Vector = { 0.1f, 0.2f, 0.3f };
        public int Dimensions => Vector.Length;
        public float[]? Embed(string text) => Vector;
    }

    private sealed class ThrowingEmbedder : IAiEmbedder
    {
        public int Dimensions => 3;
        public float[]? Embed(string text) => throw new InvalidOperationException("model offline");
    }

    private sealed class FakePlainStore : IAnalysisStore
    {
        public AnalysisRecord? LastSaved { get; private set; }
        public int LoadCorpusCalls { get; private set; }

        public long Save(AnalysisRecord record) { LastSaved = record; return 1; }
        public IReadOnlyList<CorpusEntry> LoadCorpus(int max) { LoadCorpusCalls++; return Array.Empty<CorpusEntry>(); }
        public IReadOnlyList<StoredAnalysis> GetRecent(int limit) => Array.Empty<StoredAnalysis>();
        public StoredAnalysis? GetById(long id) => null;
        public bool SetResolution(long id, string note) => false;
        public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => Array.Empty<StoredAnalysis>();
        public bool SetAutoResolution(long id, string c, string n) => false;
        public void Dispose() { }
    }

    private sealed class FakeVectorStore : IAnalysisStore, ISimilaritySearch
    {
        public const long HitId = 999;

        public AnalysisRecord? LastSaved { get; private set; }
        public int LoadCorpusCalls { get; private set; }
        public int VectorSearchCalls { get; private set; }

        public long Save(AnalysisRecord record) { LastSaved = record; return 1; }
        public IReadOnlyList<CorpusEntry> LoadCorpus(int max) { LoadCorpusCalls++; return Array.Empty<CorpusEntry>(); }
        public IReadOnlyList<StoredAnalysis> GetRecent(int limit) => Array.Empty<StoredAnalysis>();
        public StoredAnalysis? GetById(long id) => null;
        public bool SetResolution(long id, string note) => false;
        public IReadOnlyList<StoredAnalysis> GetOpenFailures(string repoId) => Array.Empty<StoredAnalysis>();
        public bool SetAutoResolution(long id, string c, string n) => false;
        public void Dispose() { }

        public IReadOnlyList<SimilarityHit> FindSimilar(float[] queryEmbedding, int topN)
        {
            VectorSearchCalls++;
            var meta = new StoredAnalysis
            {
                Id = HitId, AnalyzedAt = DateTimeOffset.UtcNow, Source = "past.log",
                Ecosystem = "generic", RuleId = "unknown", Matched = false,
                Fingerprint = "unknown:abc", LogHash = "h", Excerpt = "prior failure",
            };
            return new[] { new SimilarityHit(meta, 0.95) };
        }
    }
}
