using CiFail.Core.Storage;

namespace CiFail.Core.Output;

/// <summary>
/// Public JSON contract for a <see cref="TestStatsSnapshot"/> — emitted by
/// <c>cifail stats --tests --json</c>. PascalCase and stable, like the other contracts.
/// </summary>
public static class TestStatsJson
{
    public static TestStatsDto ToDto(TestStatsSnapshot s) => new()
    {
        TotalTestFailures = s.TotalTestFailures,
        DistinctTests = s.DistinctTests,
        FlakyCount = s.FlakyCount,
        Tests = s.Tests.Select(t => new TestStatDto
        {
            FullName = t.FullName,
            FailureCount = t.FailureCount,
            DistinctDays = t.DistinctDays,
            OpenCount = t.OpenCount,
            LastSeen = t.LastSeen,
            Flaky = t.Flaky,
        }).ToList(),
    };

    public static TestStatsSnapshot FromDto(TestStatsDto d) => new()
    {
        TotalTestFailures = d.TotalTestFailures,
        DistinctTests = d.DistinctTests,
        FlakyCount = d.FlakyCount,
        Tests = d.Tests.Select(t => new TestStat
        {
            FullName = t.FullName,
            FailureCount = t.FailureCount,
            DistinctDays = t.DistinctDays,
            OpenCount = t.OpenCount,
            LastSeen = t.LastSeen,
            Flaky = t.Flaky,
        }).ToList(),
    };

    public sealed class TestStatsDto
    {
        public int TotalTestFailures { get; init; }
        public int DistinctTests { get; init; }
        public int FlakyCount { get; init; }
        public List<TestStatDto> Tests { get; init; } = new();
    }

    public sealed class TestStatDto
    {
        public string FullName { get; init; } = "";
        public int FailureCount { get; init; }
        public int DistinctDays { get; init; }
        public int OpenCount { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public bool Flaky { get; init; }
    }
}
