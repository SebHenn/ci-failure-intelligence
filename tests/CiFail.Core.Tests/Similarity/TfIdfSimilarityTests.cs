using CiFail.Core.Similarity;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Similarity;

public class TfIdfSimilarityTests
{
    private const string NuGetFail =
        "error NU1101 unable to find package Newtonsoft.Json no packages exist in source nuget.org";
    private const string NuGetFailVariant =
        "error NU1101 unable to find package Serilog no packages exist in source nuget.org";
    private const string PythonFail =
        "Traceback most recent call last ModuleNotFoundError no module named requests pip install";

    [Fact]
    public void Near_duplicate_logs_score_higher_than_unrelated_logs()
    {
        var query = Tokenizer.Tokenize(NuGetFail);
        var similarTerms = Tokenizer.Tokenize(NuGetFailVariant);
        var unrelatedTerms = Tokenizer.Tokenize(PythonFail);

        var similar = TfIdfSimilarity.Cosine(query, similarTerms);
        var unrelated = TfIdfSimilarity.Cosine(query, unrelatedTerms);

        similar.Should().BeGreaterThan(unrelated);
        similar.Should().BeGreaterThan(0.3);
        unrelated.Should().BeLessThan(0.2);
    }

    [Fact]
    public void Rank_returns_most_similar_first_and_respects_topN()
    {
        var query = Tokenizer.Tokenize(NuGetFail);
        var corpus = new List<(int, IReadOnlyDictionary<string, int>)>
        {
            (1, Tokenizer.Tokenize(PythonFail)),
            (2, Tokenizer.Tokenize(NuGetFailVariant)),
            (3, Tokenizer.Tokenize("completely different unrelated text about widgets")),
        };

        var ranked = TfIdfSimilarity.Rank(query, corpus, topN: 2);

        ranked.Should().HaveCountLessThanOrEqualTo(2);
        ranked[0].Item.Should().Be(2); // the NuGet variant is most similar
    }

    [Fact]
    public void Empty_corpus_returns_no_results()
    {
        var query = Tokenizer.Tokenize(NuGetFail);
        TfIdfSimilarity.Rank(query,
            Array.Empty<(int, IReadOnlyDictionary<string, int>)>(), 5)
            .Should().BeEmpty();
    }
}
