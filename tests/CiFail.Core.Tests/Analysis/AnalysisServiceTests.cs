using CiFail.Core.Analysis;
using CiFail.Core.Models;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests.Analysis;

public class AnalysisServiceTests
{
    private static readonly AnalysisService Service = AnalysisService.CreateDefault();

    [Fact]
    public void Analyze_returns_root_cause_and_fingerprint_for_known_failure()
    {
        var analysis = Service.Analyze("nu1101", Fixtures.Load("nuget-nu1101.log"));

        analysis.HasMatch.Should().BeTrue();
        analysis.Ecosystem.Should().Be(Ecosystem.Dotnet);
        analysis.RootCause!.Rule.Id.Should().Be("nuget-nu1101");
        analysis.Fingerprint.RuleId.Should().Be("nuget-nu1101");
        analysis.Fingerprint.Hash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Analyze_marks_unknown_failures_and_still_fingerprints()
    {
        var analysis = Service.Analyze("unknown", Fixtures.Load("unknown.log"));

        analysis.HasMatch.Should().BeFalse();
        analysis.Fingerprint.RuleId.Should().Be("unknown");
        analysis.Fingerprint.Hash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Fingerprint_is_stable_across_cosmetic_differences()
    {
        var first = Service.Analyze("a", Fixtures.Load("nuget-nu1101.log"));
        var second = Service.Analyze("b",
            Fixtures.Load("nuget-nu1101.log").Replace("412 ms", "999 ms"));

        second.Fingerprint.ToString().Should().Be(first.Fingerprint.ToString());
    }
}
