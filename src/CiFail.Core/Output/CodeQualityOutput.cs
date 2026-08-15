using System.Text.Json;

namespace CiFail.Core.Output;

/// <summary>
/// GitLab Code Quality report (the CodeClimate JSON schema GitLab consumes via
/// <c>artifacts:reports:codequality</c>).
///
/// <para>
/// GitLab has no SARIF ingestion for anything but its own security scanners, so the SARIF report
/// cifail already produces is invisible there. This is the equivalent native surface: findings
/// show up in the merge-request widget, and GitLab dedupes them across pipelines by fingerprint
/// — which cifail already has a stable one for.
/// </para>
///
/// <para>
/// Hand-rolled here for the same reason <see cref="SarifOutput"/> and
/// <see cref="PrometheusOutput"/> are: it is a one-way projection of an already-computed result
/// onto someone else's schema, and Core takes no dependency for that.
/// </para>
/// </summary>
public static class CodeQualityOutput
{
    /// <summary>Render the analyses as a Code Quality JSON array (always an array, `[]` included).</summary>
    public static string Build(IReadOnlyList<AnalysisJson.AnalysisDto> analyses)
    {
        var issues = new List<Issue>(analyses.Count);

        foreach (var a in analyses)
        {
            var rc = a.RootCause;

            issues.Add(new Issue
            {
                Description = rc is null
                    ? "cifail did not recognize this failure."
                    : string.IsNullOrWhiteSpace(rc.Fix) ? rc.Title : $"{rc.Title} — {FirstLine(rc.Fix)}",
                CheckName = rc?.RuleId ?? "cifail-unmatched",
                // GitLab keys deduplication and "new vs existing" on this, so cifail's own
                // fingerprint is exactly the right value — it is stable across runs by design.
                Fingerprint = a.Fingerprint,
                Severity = Severity(rc),
                Location = new Location
                {
                    Path = ReportPath(a.Source),
                    Lines = new Lines { Begin = rc?.LineNumber ?? 1 },
                },
            });
        }

        return JsonSerializer.Serialize(issues, Options);
    }

    /// <summary>
    /// camelCase and no indentation: unlike cifail's own <c>--json</c>, this document's shape is
    /// dictated by GitLab, and it is machine-read rather than human-read.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Map to GitLab's severity vocabulary, preferring the rule's declared severity (R36) over
    /// the confidence buckets — the same precedence <see cref="ReportFormatting.SarifLevel"/> uses,
    /// so the two reports can't disagree about how bad something is.
    /// </summary>
    private static string Severity(AnalysisJson.MatchDto? rc)
    {
        if (rc is null) return "info";

        return ReportFormatting.SarifLevel(rc.Confidence, rc.Severity) switch
        {
            "error" => "major",
            "warning" => "minor",
            _ => "info",
        };
    }

    /// <summary>
    /// The file the finding points at. A report-expanded test source is <c>file::TestName</c>, so
    /// the file half is what GitLab can resolve — matching <see cref="SarifOutput"/>'s handling.
    /// </summary>
    private static string ReportPath(string source)
    {
        var separator = source.IndexOf("::", StringComparison.Ordinal);
        var path = separator < 0 ? source : source[..separator];
        return string.IsNullOrWhiteSpace(path) ? "stdin" : path.Replace('\\', '/');
    }

    /// <summary>
    /// Fix text is a paragraph; the widget shows one line. Keep the first sentence-ish, which is
    /// where every shipped rule puts the actionable part.
    /// </summary>
    private static string FirstLine(string fix)
    {
        var line = fix.TrimStart().Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..200].TrimEnd() + "…";
    }

    private sealed class Issue
    {
        public string Description { get; init; } = "";
        public string CheckName { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public string Severity { get; init; } = "info";
        public Location Location { get; init; } = new();
    }

    private sealed class Location
    {
        public string Path { get; init; } = "";
        public Lines Lines { get; init; } = new();
    }

    private sealed class Lines
    {
        public int Begin { get; init; } = 1;
    }
}
