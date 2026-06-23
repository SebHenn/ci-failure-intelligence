using CiFail.Core.Similarity;
using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Pure single-link agglomerative clustering of failures by TF-IDF cosine similarity over their
/// already-scrubbed term bags — the single source of truth (mirrors <see cref="StatsComputer"/>),
/// used both by store implementations of <see cref="IClusterer"/> and by the in-app fallback.
///
/// To stay near-linear up to the scan limit it blocks comparisons with an inverted index: only
/// failures that share a reasonably distinctive term are compared (ubiquitous and unique terms
/// carry no signal for pairing). Pairs at or above the threshold are merged with union-find, so a
/// chain A~B~C lands in one cluster even if A and C aren't directly similar.
/// </summary>
public static class FailureClusterer
{
    public static ClusterResult Compute(IReadOnlyList<CorpusEntry> corpus, ClusterQuery query)
    {
        var filtered = corpus
            .Where(e => query.Since is null || e.Meta.AnalyzedAt >= query.Since)
            .Where(e => query.RepoId is null || string.Equals(e.Meta.RepoId, query.RepoId, StringComparison.Ordinal))
            .Where(e => e.Terms.Count > 0)
            .Take(Math.Max(1, query.ScanLimit))
            .ToList();

        int n = filtered.Count;
        if (n == 0)
            return new ClusterResult { Clusters = Array.Empty<FailureCluster>(), TotalAnalyzed = 0 };

        var idf = TfIdfSimilarity.Idf(filtered.Select(e => e.Terms).ToList());
        var vectors = filtered.Select(e => TfIdfSimilarity.WeightVector(e.Terms, idf)).ToList();

        var uf = new UnionFind(n);
        foreach (var bucket in BuildBlocks(filtered))
        {
            for (int a = 0; a < bucket.Count; a++)
                for (int b = a + 1; b < bucket.Count; b++)
                {
                    int i = bucket[a], j = bucket[b];
                    if (uf.Find(i) == uf.Find(j)) continue; // already joined (e.g. via another term)
                    if (TfIdfSimilarity.CosineVectors(vectors[i], vectors[j]) >= query.Threshold)
                        uf.Union(i, j);
                }
        }

        var byRoot = new Dictionary<int, List<StoredAnalysis>>();
        for (int i = 0; i < n; i++)
            (byRoot.TryGetValue(uf.Find(i), out var list) ? list : byRoot[uf.Find(i)] = new()).Add(filtered[i].Meta);

        var clusters = byRoot.Values
            .Select(ToCluster)
            .Where(c => query.IncludeSingletons || c.Count > 1)
            .OrderByDescending(c => c.Count).ThenByDescending(c => c.LastSeen)
            .ToList();

        if (query.Top > 0) clusters = clusters.Take(query.Top).ToList();

        return new ClusterResult { Clusters = clusters, TotalAnalyzed = n };
    }

    private static FailureCluster ToCluster(IReadOnlyList<StoredAnalysis> rows)
    {
        // Label = the most common rule id, preferring matched rows; "unmatched" when none matched.
        var label = rows.Where(r => r.Matched && !string.IsNullOrWhiteSpace(r.RuleId))
            .GroupBy(r => r.RuleId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key ?? "unmatched";

        var ecosystems = rows
            .Select(r => string.IsNullOrWhiteSpace(r.Ecosystem) ? "unknown" : r.Ecosystem)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        return new FailureCluster
        {
            Label = label,
            Count = rows.Count,
            MemberIds = rows.Select(r => r.Id).OrderByDescending(id => id).ToList(),
            Ecosystems = ecosystems,
            LastSeen = rows.Max(r => r.AnalyzedAt),
        };
    }

    /// <summary>
    /// Build candidate-comparison blocks: invert each reasonably distinctive term to the doc indices
    /// that contain it. Terms appearing in every (or almost every) doc carry no pairing signal and
    /// would create one giant O(n²) bucket, so they're skipped; so are terms in a single doc.
    /// </summary>
    private static List<List<int>> BuildBlocks(IReadOnlyList<CorpusEntry> docs)
    {
        int n = docs.Count;
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            foreach (var term in docs[i].Terms.Keys)
                (postings.TryGetValue(term, out var list) ? list : postings[term] = new()).Add(i);

        // A term is useful for blocking when 2 <= df <= ceil(0.8 * n): shared by some docs but not all.
        int maxDf = Math.Max(2, (int)Math.Ceiling(0.8 * n));
        return postings.Values
            .Where(ids => ids.Count >= 2 && ids.Count <= maxDf)
            .ToList();
    }

    /// <summary>Disjoint-set with path compression + union by size.</summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _size = new int[n];
            for (int i = 0; i < n; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; }
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }
}
