using CiFail.Cli.Output;
using CiFail.Core.Ingest.Reports;
using Spectre.Console;
using Analysis = CiFail.Core.Models.Analysis;

namespace CiFail.Cli.Commands;

/// <summary>
/// Reporting the rule problems an analysis ran into (an invalid pattern, or one abandoned for
/// exceeding <see cref="CiFail.Core.Rules.RuleEngine.MatchTimeout"/>).
///
/// <para>
/// Shared by <c>analyze</c> and <c>gate</c> for the same reason <see cref="AnalysisInputs"/> is:
/// a rule that silently stopped working must look identical whichever command you ran. It goes to
/// <b>stderr</b> — this is a fact about the run, not part of the answer.
/// </para>
/// </summary>
internal static class RuleDiagnostics
{
    /// <summary>
    /// Warn once per distinct problem. A structured report expands into one analysis unit per
    /// failing test (R17), so a single bad rule would otherwise print the same line hundreds of
    /// times — and the count is noise: the rule is either working or it isn't.
    /// </summary>
    public static void Report(IEnumerable<Analysis> results)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in results.SelectMany(r => r.Warnings))
        {
            if (seen.Add(warning))
                CliConsole.Warn(Markup.Escape(warning));
        }
    }
}

/// <summary>
/// One thing to analyze: a whole log, or a single failing test expanded out of a JUnit/TRX
/// report (R17). <see cref="Test"/> is null for a raw log.
/// </summary>
/// <param name="Source">Display/identity for the unit — a path, <c>stdin</c>, or <c>path::TestName</c>.</param>
/// <param name="Text">The text the pipeline analyzes.</param>
/// <param name="Test">The parsed failing test, when this unit came from a report.</param>
internal readonly record struct AnalysisUnit(string Source, string Text, TestFailure? Test);

/// <summary>
/// Turning command-line paths (or stdin) into <see cref="AnalysisUnit"/>s.
///
/// <para>
/// Shared by <c>analyze</c> and <c>gate</c> so the two cannot disagree about what "one
/// failure" is — a gate that counted units differently from the command that produced the
/// baseline would fail on failures nothing had changed about.
/// </para>
/// </summary>
internal static class AnalysisInputs
{
    /// <summary>
    /// Read every path, or stdin when no paths are given. An empty result means nothing was
    /// piped and no path was passed; the caller reports that as a usage error, because the
    /// right message differs per command.
    /// </summary>
    /// <exception cref="FileNotFoundException">A named path does not exist.</exception>
    public static List<(string Source, string Text)> Read(IReadOnlyList<string> paths)
    {
        var inputs = new List<(string, string)>();

        if (paths.Count == 0)
        {
            if (!Console.IsInputRedirected)
                return inputs;
            var stdin = Console.In.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(stdin))
                inputs.Add(("stdin", stdin));
            return inputs;
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"file not found: {path}");
            inputs.Add((path, File.ReadAllText(path)));
        }

        return inputs;
    }

    /// <summary>
    /// Expand raw inputs into analysis units. A log becomes one unit; a JUnit/TRX report becomes
    /// one unit per failing test. In <c>auto</c> the format is sniffed per input.
    /// </summary>
    /// <exception cref="System.Xml.XmlException">A report was malformed.</exception>
    public static List<AnalysisUnit> BuildUnits(
        IReadOnlyList<(string Source, string Text)> inputs, ReportFormat requested)
    {
        var units = new List<AnalysisUnit>();
        foreach (var (source, text) in inputs)
        {
            var fmt = requested == ReportFormat.Auto ? TestReportParser.Detect(source, text) : requested;
            if (fmt is ReportFormat.Log or ReportFormat.Auto)
            {
                units.Add(new AnalysisUnit(source, text, null));
                continue;
            }

            foreach (var failure in TestReportParser.ParseFailures(fmt, text))
                units.Add(new AnalysisUnit($"{source}::{failure.FullName}", failure.ToLogText(), failure));
        }
        return units;
    }
}
