using System.Text;

namespace CiFail.Core.Output;

/// <summary>
/// Renders analyses as a clean Markdown report (R24) — built from the stable
/// <see cref="AnalysisJson.AnalysisDto"/> contract, mirroring the plain, beginner-oriented wording
/// of the console renderer. Suitable for a GitHub Actions step summary or a PR/MR comment.
/// </summary>
public static class MarkdownOutput
{
    public static string Build(IReadOnlyList<AnalysisJson.AnalysisDto> analyses)
    {
        var sb = new StringBuilder();
        var matched = analyses.Count(a => a.Matched);

        sb.AppendLine("## cifail analysis");
        sb.AppendLine();
        sb.AppendLine($"Analyzed **{analyses.Count}** {(analyses.Count == 1 ? "log" : "logs")}, " +
                      $"matched a known failure in **{matched}**.");
        sb.AppendLine();

        foreach (var a in analyses)
        {
            var rc = a.RootCause;
            if (rc is not null)
            {
                sb.AppendLine($"### {Inline(rc.Title)}");
                sb.AppendLine();
                sb.AppendLine($"- **Source:** `{Inline(a.Source)}`");
                sb.AppendLine($"- **Confidence:** {ReportFormatting.Confidence(rc.Confidence)} · **Category:** {Inline(rc.Category)}");
                if (!string.IsNullOrWhiteSpace(rc.MatchedLine))
                {
                    sb.AppendLine();

                    // Say where, and how often, when we know (R34).
                    var where = rc.LineNumber is > 0 ? $" (line {rc.LineNumber})" : string.Empty;
                    var often = rc.OccurrenceCount is > 1 ? $" — {rc.OccurrenceCount} occurrences" : string.Empty;
                    sb.AppendLine($"**What broke**{where}{often}");

                    sb.AppendLine();
                    sb.AppendLine("```");
                    // The context block, not the bare line: on a PR comment or a step summary the
                    // reader has no terminal to go and look at the original log in.
                    foreach (var line in ContextBlock(rc))
                        sb.AppendLine(line);
                    sb.AppendLine("```");
                }
                if (!string.IsNullOrWhiteSpace(rc.Fix))
                {
                    sb.AppendLine();
                    sb.AppendLine("**How to fix it**");
                    sb.AppendLine();
                    sb.AppendLine(rc.Fix.Trim());
                }
                if (!string.IsNullOrWhiteSpace(rc.Docs))
                {
                    sb.AppendLine();
                    sb.AppendLine($"📖 {rc.Docs}");
                }
            }
            else
            {
                sb.AppendLine($"### No known rule matched — `{Inline(a.Source)}`");
                sb.AppendLine();
                sb.AppendLine("cifail didn't recognize this failure. The cause is in the log output; " +
                              "consider adding a rule (`cifail suggest-rule`).");
            }

            if (a.SimilarFailures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Seen before**");
                sb.AppendLine();
                foreach (var s in a.SimilarFailures.Take(3))
                {
                    var when = s.AnalyzedAt.ToString("yyyy-MM-dd");
                    var note = string.IsNullOrWhiteSpace(s.Resolution) ? "" : $" — fixed: {Inline(s.Resolution!)}";
                    sb.AppendLine($"- #{s.Id} ({when}){note}");
                }
            }

            if (a.Ai is { } ai && (!string.IsNullOrWhiteSpace(ai.RootCause) || !string.IsNullOrWhiteSpace(ai.Fix)))
            {
                sb.AppendLine();
                sb.AppendLine($"**AI suggestion** ({Inline(ai.Model)})");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(ai.RootCause)) sb.AppendLine(ai.RootCause.Trim());
                if (!string.IsNullOrWhiteSpace(ai.Fix))
                {
                    sb.AppendLine();
                    sb.AppendLine(ai.Fix.Trim());
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The matched line with its surrounding log, oldest first. Inside a fenced block, so no
    /// escaping is needed — but a stray fence in the log would end it early, hence the guard.
    /// </summary>
    private static IEnumerable<string> ContextBlock(AnalysisJson.MatchDto rc)
    {
        var lines = (rc.ContextBefore ?? new List<string>())
            .Append(rc.MatchedLine.Trim())
            .Concat(rc.ContextAfter ?? new List<string>());

        return lines.Select(l => l.TrimEnd().Replace("```", "'''"));
    }

    /// <summary>Neutralize characters that would break a single-line Markdown cell/inline span.</summary>
    private static string Inline(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
}
