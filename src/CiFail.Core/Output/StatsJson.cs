using CiFail.Core.Storage;

namespace CiFail.Core.Output;

/// <summary>
/// Public JSON contract for a <see cref="StatsSnapshot"/> — emitted by <c>cifail stats --json</c>
/// and the <c>cifail serve</c> <c>GET /stats</c> endpoint, and round-tripped by the matching
/// <see cref="HttpAnalysisStore"/> client. PascalCase and stable, like the other contracts.
/// Durations are expressed as seconds (doubles) to avoid TimeSpan serialization quirks.
/// </summary>
public static class StatsJson
{
    public static StatsDto ToDto(StatsSnapshot s) => new()
    {
        Total = s.Total,
        Open = s.Open,
        Resolved = s.Resolved,
        Unmatched = s.Unmatched,
        ByEcosystem = s.ByEcosystem.Select(c => new CountDto { Key = c.Key, Count = c.Count }).ToList(),
        TopFailures = s.TopFailures.Select(ToFpDto).ToList(),
        RecurrenceRate = Math.Round(s.RecurrenceRate, 4),
        MeanTimeToResolutionSeconds = s.MeanTimeToResolution?.TotalSeconds,
        ResolvedWithTiming = s.ResolvedWithTiming,
        Flaky = s.Flaky.Select(ToFpDto).ToList(),
    };

    public static StatsSnapshot FromDto(StatsDto d) => new()
    {
        Total = d.Total,
        Open = d.Open,
        Resolved = d.Resolved,
        Unmatched = d.Unmatched,
        ByEcosystem = d.ByEcosystem.Select(c => new CountByKey(c.Key, c.Count)).ToList(),
        TopFailures = d.TopFailures.Select(FromFpDto).ToList(),
        RecurrenceRate = d.RecurrenceRate,
        MeanTimeToResolution = d.MeanTimeToResolutionSeconds is { } secs
            ? TimeSpan.FromSeconds(secs)
            : null,
        ResolvedWithTiming = d.ResolvedWithTiming,
        Flaky = d.Flaky.Select(FromFpDto).ToList(),
    };

    private static FingerprintDto ToFpDto(FingerprintStat f) => new()
    {
        Fingerprint = f.Fingerprint,
        RuleId = f.RuleId,
        Count = f.Count,
        OpenCount = f.OpenCount,
        LastSeen = f.LastSeen,
        Flaky = f.Flaky,
    };

    private static FingerprintStat FromFpDto(FingerprintDto d) => new()
    {
        Fingerprint = d.Fingerprint,
        RuleId = d.RuleId,
        Count = d.Count,
        OpenCount = d.OpenCount,
        LastSeen = d.LastSeen,
        Flaky = d.Flaky,
    };

    public sealed class StatsDto
    {
        public int Total { get; init; }
        public int Open { get; init; }
        public int Resolved { get; init; }
        public int Unmatched { get; init; }
        public List<CountDto> ByEcosystem { get; init; } = new();
        public List<FingerprintDto> TopFailures { get; init; } = new();
        public double RecurrenceRate { get; init; }
        public double? MeanTimeToResolutionSeconds { get; init; }
        public int ResolvedWithTiming { get; init; }
        public List<FingerprintDto> Flaky { get; init; } = new();
    }

    public sealed class CountDto
    {
        public string Key { get; init; } = "";
        public int Count { get; init; }
    }

    public sealed class FingerprintDto
    {
        public string Fingerprint { get; init; } = "";
        public string RuleId { get; init; } = "";
        public int Count { get; init; }
        public int OpenCount { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public bool Flaky { get; init; }
    }
}
