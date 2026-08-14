using System.Text.Json;
using System.Text.Json.Serialization;

namespace CiFail.Core.Output;

/// <summary>
/// Renders analyses as a SARIF 2.1.0 log (R24), so GitHub Code Scanning (and other SARIF consumers)
/// render cifail's findings natively. Built from the stable <see cref="AnalysisJson.AnalysisDto"/>
/// contract — no domain coupling. Multiple analysis units (e.g. a JUnit report expanded into many
/// failing tests) map into one <c>run</c> with one <c>result</c> each.
/// </summary>
public static class SarifOutput
{
    private const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string ToolUri = "https://github.com/SebHenn/ci-failure-intelligence";
    private const string UnmatchedRuleId = "cifail-unmatched";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Build(IReadOnlyList<AnalysisJson.AnalysisDto> analyses)
    {
        var ruleIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var rules = new List<object>();
        var results = new List<object>();

        foreach (var a in analyses)
        {
            var rc = a.RootCause;
            string ruleId;
            string level;
            string message;

            if (rc is not null)
            {
                ruleId = rc.RuleId;
                level = ReportFormatting.SarifLevel(rc.Confidence);
                message = string.IsNullOrWhiteSpace(rc.Fix) ? rc.Title : $"{rc.Title} — {rc.Fix}";
                if (!ruleIndex.ContainsKey(ruleId))
                {
                    ruleIndex[ruleId] = rules.Count;
                    rules.Add(new Dictionary<string, object?>
                    {
                        ["id"] = ruleId,
                        ["name"] = rc.Title,
                        ["shortDescription"] = new { text = rc.Title },
                        ["fullDescription"] = string.IsNullOrWhiteSpace(rc.Fix) ? null : new { text = rc.Fix },
                        ["helpUri"] = rc.Docs,
                        ["defaultConfiguration"] = new { level },
                        ["properties"] = new { category = rc.Category },
                    });
                }
            }
            else
            {
                ruleId = UnmatchedRuleId;
                level = "note";
                message = "No known cifail rule matched this log.";
                if (!ruleIndex.ContainsKey(ruleId))
                {
                    ruleIndex[ruleId] = rules.Count;
                    rules.Add(new Dictionary<string, object?>
                    {
                        ["id"] = ruleId,
                        ["name"] = "Unmatched failure",
                        ["shortDescription"] = new { text = "No known cifail rule matched this failure." },
                        ["defaultConfiguration"] = new { level = "note" },
                    });
                }
            }

            results.Add(new Dictionary<string, object?>
            {
                ["ruleId"] = ruleId,
                ["ruleIndex"] = ruleIndex[ruleId],
                ["level"] = level,
                ["message"] = new { text = message },
                ["locations"] = new[]
                {
                    new
                    {
                        physicalLocation = new
                        {
                            artifactLocation = new { uri = ArtifactUri(a.Source), uriBaseId = "SRCROOT" },
                            // The line the rule actually matched (R34), with the matched text as
                            // the snippet. Every result used to be pinned to line 1 because
                            // nothing tracked line numbers — which put every Code Scanning
                            // annotation at the top of the file regardless of where the failure was.
                            region = Region(a),
                        },
                    },
                },
                ["partialFingerprints"] = new Dictionary<string, string> { ["cifailFingerprint/v1"] = a.Fingerprint },
            });
        }

        var root = new Dictionary<string, object?>
        {
            ["$schema"] = SchemaUri,
            ["version"] = "2.1.0",
            ["runs"] = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new Dictionary<string, object?>
                        {
                            ["name"] = "cifail",
                            ["informationUri"] = ToolUri,
                            ["version"] = ToolVersion(),
                            ["rules"] = rules,
                        },
                    },
                    results,
                },
            },
        };

        return JsonSerializer.Serialize(root, Options);
    }

    /// <summary>
    /// The artifact URI for a result. A report-expanded unit's source is <c>file::TestName</c>, so
    /// take the file part; relativize to the working dir for Code Scanning to anchor it; for stdin
    /// (no real file) emit a stable placeholder.
    /// </summary>
    /// <summary>
    /// The SARIF <c>region</c> for a result: the matched line, with the matched text as a
    /// <c>snippet</c> so a viewer can show the evidence without fetching the artifact — which
    /// matters here because the "artifact" is usually a CI log that no longer exists.
    /// </summary>
    private static object Region(AnalysisJson.AnalysisDto a)
    {
        var line = a.RootCause?.LineNumber ?? 0;
        var snippet = a.RootCause?.MatchedLine;

        // SARIF startLine is 1-based and must be positive; fall back to 1 for an unmatched
        // failure, which has no line of its own to point at.
        if (line <= 0)
            return new { startLine = 1 };

        return string.IsNullOrWhiteSpace(snippet)
            ? new { startLine = line }
            : new { startLine = line, snippet = new { text = snippet } };
    }

    private static string ArtifactUri(string source)
    {
        var sep = source.IndexOf("::", StringComparison.Ordinal);
        var file = sep >= 0 ? source[..sep] : source;
        if (string.IsNullOrWhiteSpace(file) || file == "stdin" || file == "upload")
            return "stdin";

        try
        {
            var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(file));
            return rel.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return file.Replace('\\', '/');
        }
    }

    private static string ToolVersion() =>
        typeof(SarifOutput).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
