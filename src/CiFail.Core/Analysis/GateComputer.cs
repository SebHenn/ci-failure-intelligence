namespace CiFail.Core.Analysis;

/// <summary>One failure seen in the current run, reduced to what the gate needs.</summary>
/// <param name="Fingerprint">The run's <c>ruleId:hash</c> identity.</param>
/// <param name="Source">Where it came from — a path, <c>stdin</c>, or <c>path::TestName</c>.</param>
/// <param name="RuleId">Winning rule id, or <c>unknown</c> when nothing matched.</param>
/// <param name="Title">The rule's title, when a rule matched.</param>
public sealed record ObservedFailure(string Fingerprint, string Source, string RuleId, string? Title);

/// <summary>A distinct failure in the gate's verdict, with every place it showed up.</summary>
public sealed record GateFinding(string Fingerprint, string RuleId, string? Title, IReadOnlyList<string> Sources)
{
    /// <summary>How many analysis units produced this same fingerprint in one run.</summary>
    public int Count => Sources.Count;
}

/// <summary>The gate's verdict for one run.</summary>
/// <param name="New">Failures absent from the baseline. Non-empty means the gate fails.</param>
/// <param name="Known">Failures the baseline already accepts.</param>
/// <param name="Stale">Baseline entries this run did not reproduce — candidates for removal.</param>
public sealed record GateResult(
    IReadOnlyList<GateFinding> New,
    IReadOnlyList<GateFinding> Known,
    IReadOnlyList<string> Stale)
{
    /// <summary>True when nothing new showed up.</summary>
    public bool Passed => New.Count == 0;
}

/// <summary>
/// Compares the failures of one run against a committed baseline, so CI can fail on a
/// <b>new</b> failure without failing on the backlog everyone already knows about.
///
/// <para>
/// Pure, like <see cref="StatsComputer"/> and <see cref="FailureClusterer"/>: no store, no
/// clock, no filesystem. That is deliberate — a gate has to give the same verdict on a
/// laptop and in a scratch container with no writable home and no database, or it stops
/// being trustworthy exactly when it matters.
/// </para>
/// </summary>
public static class GateComputer
{
    /// <summary>
    /// Split this run's failures into new and known, and report which baseline entries no
    /// longer occur.
    /// </summary>
    /// <param name="observed">Every failure from the current run; duplicates are grouped.</param>
    /// <param name="baseline">Accepted fingerprints. An empty set makes every failure new.</param>
    public static GateResult Evaluate(IEnumerable<ObservedFailure> observed, IReadOnlySet<string> baseline)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(baseline);

        // Grouped by fingerprint, but kept in first-seen order: the report then reads in the
        // same order as the log it came from. Ordinal, because a fingerprint is an exact id.
        var groups = new Dictionary<string, List<ObservedFailure>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var failure in observed)
        {
            if (!groups.TryGetValue(failure.Fingerprint, out var list))
            {
                groups[failure.Fingerprint] = list = new List<ObservedFailure>();
                order.Add(failure.Fingerprint);
            }
            list.Add(failure);
        }

        var fresh = new List<GateFinding>();
        var known = new List<GateFinding>();

        foreach (var fingerprint in order)
        {
            var occurrences = groups[fingerprint];
            var finding = new GateFinding(
                fingerprint,
                occurrences[0].RuleId,
                occurrences[0].Title,
                occurrences.Select(o => o.Source).ToList());

            (baseline.Contains(fingerprint) ? known : fresh).Add(finding);
        }

        // Sorted: this is advisory output that people diff between runs, not a narrative.
        var stale = baseline
            .Where(f => !groups.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return new GateResult(fresh, known, stale);
    }
}
