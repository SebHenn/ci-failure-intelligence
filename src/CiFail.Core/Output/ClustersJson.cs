using CiFail.Core.Storage;

namespace CiFail.Core.Output;

/// <summary>
/// Public JSON contract for a <see cref="ClusterResult"/> — emitted by <c>cifail clusters --json</c>
/// and the <c>cifail serve</c> <c>GET /clusters</c> endpoint, round-tripped by the matching
/// <see cref="HttpAnalysisStore"/> client. PascalCase and stable, like the other contracts.
/// </summary>
public static class ClustersJson
{
    public static ClusterResultDto ToDto(ClusterResult r) => new()
    {
        TotalAnalyzed = r.TotalAnalyzed,
        Clusters = r.Clusters.Select(c => new ClusterDto
        {
            Label = c.Label,
            Count = c.Count,
            MemberIds = c.MemberIds.ToList(),
            Ecosystems = c.Ecosystems.ToList(),
            LastSeen = c.LastSeen,
        }).ToList(),
    };

    public static ClusterResult FromDto(ClusterResultDto d) => new()
    {
        TotalAnalyzed = d.TotalAnalyzed,
        Clusters = d.Clusters.Select(c => new FailureCluster
        {
            Label = c.Label,
            Count = c.Count,
            MemberIds = c.MemberIds,
            Ecosystems = c.Ecosystems,
            LastSeen = c.LastSeen,
        }).ToList(),
    };

    public sealed class ClusterResultDto
    {
        public int TotalAnalyzed { get; init; }
        public List<ClusterDto> Clusters { get; init; } = new();
    }

    public sealed class ClusterDto
    {
        public string Label { get; init; } = "";
        public int Count { get; init; }
        public List<long> MemberIds { get; init; } = new();
        public List<string> Ecosystems { get; init; } = new();
        public DateTimeOffset LastSeen { get; init; }
    }
}
