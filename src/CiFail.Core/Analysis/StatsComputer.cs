using CiFail.Core.Storage;

namespace CiFail.Core.Analysis;

/// <summary>
/// Pure aggregation over stored analyses — the single source of truth for stats, used both
/// by store implementations of <see cref="IAnalysisStats"/> and by the in-app fallback so
/// every backend produces identical numbers.
/// </summary>
public static class StatsComputer
{
    /// <summary>Compute a <see cref="StatsSnapshot"/> from a set of rows, applying the query filters.</summary>
    public static StatsSnapshot Compute(IEnumerable<StoredAnalysis> rows, StatsQuery query)
    {
        var filtered = rows
            .Where(r => query.Since is null || r.AnalyzedAt >= query.Since)
            .Where(r => query.RepoId is null || string.Equals(r.RepoId, query.RepoId, StringComparison.Ordinal))
            .ToList();

        var total = filtered.Count;
        var resolved = filtered.Count(r => r.Status == AnalysisStatus.Resolved);
        var open = total - resolved;
        var unmatched = filtered.Count(r => !r.Matched);

        var byEcosystem = filtered
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Ecosystem) ? "unknown" : r.Ecosystem)
            .Select(g => new CountByKey(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        var groups = filtered
            .GroupBy(r => r.Fingerprint, StringComparer.Ordinal)
            .Select(g => ToStat(g.Key, g.ToList()))
            .ToList();

        var distinct = groups.Count;
        var recurring = groups.Count(g => g.Count > 1);
        var recurrenceRate = distinct == 0 ? 0.0 : (double)recurring / distinct;

        var topFailures = groups
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.LastSeen)
            .Take(Math.Max(1, query.Top))
            .ToList();

        var flaky = groups
            .Where(g => g.Flaky)
            .OrderByDescending(g => g.LastSeen)
            .ToList();

        // MTTR: per resolved row, ResolvedAt - AnalyzedAt (only where both are known and sane).
        var durations = filtered
            .Where(r => r.ResolvedAt is not null && r.ResolvedAt >= r.AnalyzedAt)
            .Select(r => r.ResolvedAt!.Value - r.AnalyzedAt)
            .ToList();
        TimeSpan? mttr = durations.Count == 0
            ? null
            : TimeSpan.FromTicks((long)durations.Average(d => d.Ticks));

        return new StatsSnapshot
        {
            Total = total,
            Open = open,
            Resolved = resolved,
            Unmatched = unmatched,
            ByEcosystem = byEcosystem,
            TopFailures = topFailures,
            RecurrenceRate = recurrenceRate,
            MeanTimeToResolution = mttr,
            ResolvedWithTiming = durations.Count,
            Flaky = flaky,
            Daily = DailyBuckets(filtered, query.DailyDays),
        };
    }

    /// <summary>
    /// Failures per day over the trailing window, oldest first.
    ///
    /// <para>
    /// Every day in the window is present, including the empty ones. A sparkline drawn from
    /// only the days that had failures is a lie: a quiet week would render as a flat line at
    /// whatever the neighbouring days were, hiding exactly the gap you wanted to see.
    /// </para>
    ///
    /// <para>
    /// Bucketed by UTC date. The alternative — the viewer's local date — would make the same
    /// history produce different numbers per browser, and these have to agree with what the
    /// CLI prints.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CountByDay> DailyBuckets(IReadOnlyList<StoredAnalysis> rows, int days)
    {
        if (days <= 0) return Array.Empty<CountByDay>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = today.AddDays(-(days - 1));

        var counts = rows
            .Select(r => DateOnly.FromDateTime(r.AnalyzedAt.UtcDateTime))
            .Where(d => d >= first && d <= today)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var buckets = new List<CountByDay>(days);
        for (var day = first; day <= today; day = day.AddDays(1))
            buckets.Add(new CountByDay(day, counts.TryGetValue(day, out var n) ? n : 0));

        return buckets;
    }

    private static FingerprintStat ToStat(string fingerprint, IReadOnlyList<StoredAnalysis> rows)
    {
        var lastSeen = rows.Max(r => r.AnalyzedAt);
        var openCount = rows.Count(r => r.Status == AnalysisStatus.Open);

        // Flaky: an occurrence appeared after this fingerprint had been resolved.
        var firstResolvedAt = rows
            .Where(r => r.ResolvedAt is not null)
            .Select(r => r.ResolvedAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        var flaky = rows.Any(r => r.AnalyzedAt > firstResolvedAt);

        // Prefer a matched rule id for the label; fall back to whatever is recorded.
        var ruleId = rows.FirstOrDefault(r => r.Matched)?.RuleId ?? rows[0].RuleId;

        return new FingerprintStat
        {
            Fingerprint = fingerprint,
            RuleId = ruleId,
            Count = rows.Count,
            OpenCount = openCount,
            LastSeen = lastSeen,
            Flaky = flaky,
        };
    }
}
