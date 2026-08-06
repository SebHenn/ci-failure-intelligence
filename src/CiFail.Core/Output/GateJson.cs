using CiFail.Core.Analysis;

namespace CiFail.Core.Output;

/// <summary>
/// Public JSON contract for a <see cref="GateResult"/> — emitted by <c>cifail gate --json</c>.
/// PascalCase and stable, like the other contracts.
/// </summary>
public static class GateJson
{
    public static GateResultDto ToDto(GateResult result, string baselinePath) => new()
    {
        Passed = result.Passed,
        BaselinePath = baselinePath,
        NewCount = result.New.Count,
        KnownCount = result.Known.Count,
        New = result.New.Select(ToDto).ToList(),
        Known = result.Known.Select(ToDto).ToList(),
        Stale = result.Stale.ToList(),
    };

    private static GateFindingDto ToDto(GateFinding f) => new()
    {
        Fingerprint = f.Fingerprint,
        RuleId = f.RuleId,
        Title = f.Title,
        Count = f.Count,
        Sources = f.Sources.ToList(),
    };

    public sealed class GateResultDto
    {
        /// <summary>False when at least one failure was not in the baseline.</summary>
        public bool Passed { get; init; }

        public string BaselinePath { get; init; } = "";
        public int NewCount { get; init; }
        public int KnownCount { get; init; }

        /// <summary>Failures absent from the baseline — the reason a gate run fails.</summary>
        public List<GateFindingDto> New { get; init; } = new();

        /// <summary>Failures the baseline already accepts.</summary>
        public List<GateFindingDto> Known { get; init; } = new();

        /// <summary>Baseline entries this run did not reproduce.</summary>
        public List<string> Stale { get; init; } = new();
    }

    public sealed class GateFindingDto
    {
        public string Fingerprint { get; init; } = "";
        public string RuleId { get; init; } = "";
        public string? Title { get; init; }

        /// <summary>How many analysis units produced this fingerprint.</summary>
        public int Count { get; init; }

        public List<string> Sources { get; init; } = new();
    }
}
