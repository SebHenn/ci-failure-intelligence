using System.IO.Compression;
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
    /// <exception cref="FileNotFoundException">A named path matched nothing.</exception>
    public static List<(string Source, string Text)> Read(IReadOnlyList<string> paths)
    {
        var inputs = new List<(string, string)>();

        if (paths.Count == 0 || paths.All(p => p == StdinToken))
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
            foreach (var file in Expand(path))
                inputs.Add((file, ReadText(file)));
        }

        return inputs;
    }

    /// <summary>The conventional "read stdin" argument, so stdin can be mixed with real paths.</summary>
    public const string StdinToken = "-";

    /// <summary>Log extensions a directory walk will pick up. A directory of anything else is noise.</summary>
    private static readonly string[] LogExtensions =
        { ".log", ".txt", ".out", ".xml", ".trx", ".gz" };

    /// <summary>
    /// Turn one command-line path into the files it names: a file, every log in a directory, or
    /// the matches of a glob.
    ///
    /// <para>
    /// All three used to be one <c>File.Exists</c> check. <c>cifail analyze logs/</c> reported
    /// "file not found: logs/", which is actively misleading, and a glob only worked when the
    /// shell had already expanded it — so the README's own
    /// <c>cifail gate --format trx TestResults/*.trx</c> failed on PowerShell and cmd, on a
    /// project whose author develops on Windows.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Expand(string path)
    {
        if (File.Exists(path))
            return new[] { path };

        if (Directory.Exists(path))
        {
            var found = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => LogExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (found.Count == 0)
                throw new FileNotFoundException(
                    $"no logs found in directory: {path} " +
                    $"(looking for {string.Join(", ", LogExtensions)})");

            return found;
        }

        if (HasGlobCharacters(path))
        {
            var matches = ExpandGlob(path);
            if (matches.Count == 0)
                throw new FileNotFoundException($"no files match: {path}");
            return matches;
        }

        throw new FileNotFoundException($"file not found: {path}");
    }

    private static bool HasGlobCharacters(string path) =>
        path.Contains('*') || path.Contains('?');

    /// <summary>
    /// Expand a glob ourselves, because the shell may not have.
    ///
    /// <para>
    /// Only the final segment may contain wildcards, which covers what people actually type
    /// (<c>logs/*.log</c>, <c>**</c> is handled by pointing at the directory instead). The
    /// directory part is resolved first so an unreadable or missing parent surfaces as "no files
    /// match" rather than an exception from deep inside the enumerator.
    /// </para>
    /// </summary>
    private static List<string> ExpandGlob(string pattern)
    {
        var directory = Path.GetDirectoryName(pattern);
        var leaf = Path.GetFileName(pattern);

        if (HasGlobCharacters(directory ?? string.Empty))
            throw new FileNotFoundException(
                $"only the file name may contain wildcards: {pattern}");

        var searchIn = string.IsNullOrEmpty(directory) ? "." : directory;
        if (!Directory.Exists(searchIn))
            return new List<string>();

        return Directory.EnumerateFiles(searchIn, leaf, SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Read a log, transparently decompressing <c>.gz</c> — which is what CI providers hand you
    /// when you download a job's log, and previously produced a screenful of mojibake analyzed as
    /// though it were text.
    /// </summary>
    private static string ReadText(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);

        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
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
