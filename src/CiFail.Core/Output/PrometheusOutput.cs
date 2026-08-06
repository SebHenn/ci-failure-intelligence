using System.Globalization;
using System.Text;
using CiFail.Core.Storage;

namespace CiFail.Core.Output;

/// <summary>
/// Renders a <see cref="StatsSnapshot"/> as Prometheus text exposition format, for
/// <c>cifail serve</c>'s <c>GET /metrics</c>.
///
/// <para>
/// Hand-rolled rather than pulled from a client library: the format is a dozen lines of
/// text, this is a one-way export of an already-computed snapshot (no counters to register,
/// no collection lifecycle), and it lives in Core where the ASP.NET-only packages aren't
/// referenced. Same reasoning as <see cref="SarifOutput"/>.
/// </para>
/// </summary>
public static class PrometheusOutput
{
    /// <summary>The exact Content-Type Prometheus expects for this format.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>
    /// Render the snapshot. Every metric is a gauge: these are aggregates recomputed from
    /// history on each scrape, not monotonically increasing process counters — calling them
    /// counters would licence Prometheus to compute rates that mean nothing.
    /// </summary>
    /// <param name="stats">The snapshot to expose.</param>
    /// <param name="topFailureLimit">
    /// How many per-fingerprint series to emit. Cardinality is the thing that kills a
    /// Prometheus server, and fingerprints are unbounded, so this is deliberately small.
    /// </param>
    public static string Build(StatsSnapshot stats, int topFailureLimit = 10)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var sb = new StringBuilder();

        Gauge(sb, "cifail_failures_total", "Analyses recorded in history.", stats.Total);
        Gauge(sb, "cifail_failures_open", "Failures not yet marked resolved.", stats.Open);
        Gauge(sb, "cifail_failures_resolved", "Failures marked resolved.", stats.Resolved);
        Gauge(sb, "cifail_failures_unmatched",
            "Failures no rule explained — a rule-coverage gap.", stats.Unmatched);
        Gauge(sb, "cifail_recurrence_rate",
            "Fraction of distinct failures seen more than once (0..1).", stats.RecurrenceRate);
        Gauge(sb, "cifail_flaky_failures",
            "Distinct failures that recurred after being resolved.", stats.Flaky.Count);

        if (stats.MeanTimeToResolution is { } mttr)
            Gauge(sb, "cifail_mean_time_to_resolution_seconds",
                "Mean time from first sighting to resolution.", mttr.TotalSeconds);

        Header(sb, "cifail_failures_by_ecosystem", "Analyses recorded per ecosystem.");
        foreach (var eco in stats.ByEcosystem)
            Sample(sb, "cifail_failures_by_ecosystem", eco.Count, ("ecosystem", eco.Key));

        // Per-fingerprint series, capped. A `fingerprint` label is high-cardinality by
        // nature; exporting all of history here would quietly hurt whoever scrapes it.
        Header(sb, "cifail_failure_occurrences",
            $"Occurrences of each of the top {topFailureLimit} recurring failures.");
        foreach (var failure in stats.TopFailures.Take(Math.Max(0, topFailureLimit)))
            Sample(sb, "cifail_failure_occurrences", failure.Count,
                ("fingerprint", failure.Fingerprint), ("rule", failure.RuleId));

        return sb.ToString();
    }

    private static void Gauge(StringBuilder sb, string name, string help, double value)
    {
        Header(sb, name, help);
        sb.Append(name).Append(' ').Append(Number(value)).Append('\n');
    }

    private static void Header(StringBuilder sb, string name, string help)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
    }

    private static void Sample(StringBuilder sb, string name, double value, params (string Key, string Value)[] labels)
    {
        sb.Append(name).Append('{');
        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(labels[i].Key).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
        }
        sb.Append("} ").Append(Number(value)).Append('\n');
    }

    /// <summary>Invariant culture: a decimal comma would make the whole document unparseable.</summary>
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Label values come from stored data (ecosystem names, rule ids, fingerprints), so they
    /// are escaped per the exposition format rather than trusted.
    /// </summary>
    private static string EscapeLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>HELP text escapes backslash and newline only — a quote is legal there.</summary>
    private static string EscapeHelp(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
