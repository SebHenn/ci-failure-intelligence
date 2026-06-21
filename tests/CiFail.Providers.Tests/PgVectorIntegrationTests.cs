using CiFail.Core.Similarity;
using CiFail.Core.Storage;
using CiFail.Providers;
using FluentAssertions;
using Testcontainers.PostgreSql;

namespace CiFail.Providers.Tests;

/// <summary>
/// Exercises the pgvector provider (R10) against a real Postgres with the <c>vector</c> extension
/// (the <c>pgvector/pgvector</c> image). Gated by <see cref="IntegrationGate"/> (CIFAIL_DB_IT=1)
/// so it only runs where Docker is available. Verifies the normal store contract still holds with
/// the vector column present, and that nearest-neighbour search returns the closest embedding.
/// </summary>
public class PgVectorIntegrationTests
{
    private const int Dim = 768; // matches PgVectorStoreProvider's default (CIFAIL_AI_EMBED_DIM)

    [SkippableFact]
    public async Task PgVector_store_satisfies_the_contract_and_searches_by_vector()
    {
        Skip.IfNot(IntegrationGate.Enabled, IntegrationGate.SkipReason);

        await using var container = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .Build();
        await container.StartAsync();

        using var store = new PgVectorStoreProvider().Create(container.GetConnectionString());

        // 1) The generic store behaviour is unchanged by the added vector column.
        StoreContract.Verify(store);

        // 2) Nearest-neighbour search returns the closest stored embedding first.
        var idA = SaveWithEmbedding(store, "rule:a", OneHot(0));
        SaveWithEmbedding(store, "rule:b", OneHot(1));
        SaveWithEmbedding(store, "rule:c", OneHot(2));

        var search = (ISimilaritySearch)store;
        var hits = search.FindSimilar(OneHot(0), topN: 2);

        hits.Should().NotBeEmpty();
        hits[0].Meta.Id.Should().Be(idA);          // the one-hot at index 0 is the exact match
        hits[0].Score.Should().BeGreaterThan(0.9);  // cosine similarity ~1
    }

    private static long SaveWithEmbedding(IAnalysisStore store, string fingerprint, float[] embedding) =>
        store.Save(new AnalysisRecord
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
            Embedding = embedding,
        });

    private static float[] OneHot(int index)
    {
        var v = new float[Dim];
        v[index] = 1f;
        return v;
    }
}
