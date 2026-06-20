using CiFail.Core.Analysis;
using CiFail.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

public class AnalysisServiceStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cifail-svc-{Guid.NewGuid():N}.db");
    private readonly SqliteAnalysisRepository _repo;
    private readonly AnalysisService _service;

    public AnalysisServiceStoreTests()
    {
        _repo = new SqliteAnalysisRepository(_dbPath);
        _service = AnalysisService.CreateWithStore(_repo);
    }

    [Fact]
    public void Second_analysis_of_similar_log_finds_the_first_as_similar()
    {
        // First run: nothing to compare against, but it is persisted.
        var first = _service.Analyze("run1", Fixtures.Load("nuget-nu1101.log"));
        first.SimilarFailures.Should().BeEmpty();

        // A near-identical failure (different package, cosmetic noise) should match.
        var variant = Fixtures.Load("nuget-nu1101.log")
            .Replace("Newtonsoft.Jsn", "Serilg")
            .Replace("412 ms", "888 ms");
        var second = _service.Analyze("run2", variant);

        second.SimilarFailures.Should().NotBeEmpty();
        second.SimilarFailures[0].RuleId.Should().Be("nuget-nu1101");
        second.SimilarFailures[0].Similarity.Should().BeGreaterThan(0.3);
    }

    [Fact]
    public void NoHistory_option_still_finds_similar_but_does_not_persist()
    {
        _service.Analyze("seed", Fixtures.Load("nuget-nu1101.log"));
        int before = _repo.GetRecent(100).Count;

        var options = new AnalysisOptions { RecordHistory = false };
        var result = _service.Analyze("transient", Fixtures.Load("nuget-nu1101.log"), options);

        result.SimilarFailures.Should().NotBeEmpty();         // similarity still queried
        _repo.GetRecent(100).Count.Should().Be(before);       // but nothing new persisted
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
