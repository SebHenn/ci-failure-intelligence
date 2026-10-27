using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using CiFail.Cli.Output;
using CiFail.Core.Analysis;
using CiFail.Core.Git;
using CiFail.Core.Storage;
using CiFail.Core.Ingest.Reports;
using CiFail.Core.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail selection-misses --tia &lt;report.json&gt; &lt;test-report&gt;...` — which of these failing
/// tests had dotnet-tia's selection skipped. Exit 0 when none, 1 when some, 2 on bad input.
/// </summary>
public sealed class SelectionMissesCommand : Command<SelectionMissesCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<reports>")]
        [Description("JUnit or TRX reports from the run that failed: a file, directory or glob, repeatable.")]
        public string[] Paths { get; init; } = Array.Empty<string>();

        [CommandOption("--tia <FILE>")]
        [Description("The dotnet-tia report of the selective run, written with `dotnet tia run --report <file> --report-skipped`.")]
        public string? Tia { get; init; }

        [CommandOption("--format <FORMAT>")]
        [Description("auto (default), junit or trx.")]
        public string Format { get; init; } = "auto";

        [CommandOption("--json")]
        [Description("Print the result as JSON.")]
        public bool Json { get; init; }

        [CommandOption("--no-history")]
        [Description("Don't look the misses up in cifail's history of failures.")]
        public bool NoHistory { get; init; }

        [CommandOption("--commit <SHA>")]
        [Description("The commit whose run failed. Its own failures are not counted as history. Defaults to HEAD.")]
        public string? Commit { get; init; }

        [CommandOption("--repo <REPO_ID>")]
        [Description("Count only this repository's history (the root-commit repo id). Defaults to the current repository's.")]
        public string? Repo { get; init; }

        [CommandOption("--expect-head <SHA>")]
        [Description("Refuse a report whose selection was not made at this commit: the pull request's head. A short SHA is enough.")]
        public string? ExpectHead { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.Tia))
        {
            CliConsole.Error("--tia is required: the dotnet-tia report of the selective run.");
            return ExitCodes.Usage;
        }

        if (TestReportParser.TryParseFormat(settings.Format) is not { } format || format == ReportFormat.Log)
        {
            CliConsole.Error($"unknown --format '{Markup.Escape(settings.Format)}' (use auto, junit, or trx).");
            return ExitCodes.Usage;
        }

        TiaSelection selection;
        List<(string Source, string Text)> inputs;
        try
        {
            selection = SelectionMisses.ReadReport(File.ReadAllText(settings.Tia));
            inputs = AnalysisInputs.Read(settings.Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            CliConsole.Error(Markup.Escape(ex is FormatException ? $"{settings.Tia}: {ex.Message}" : ex.Message));
            return ExitCodes.Usage;
        }

        if (inputs.Count == 0)
        {
            CliConsole.Error("no test report given. Pass the JUnit or TRX files of the run that failed.");
            return ExitCodes.Usage;
        }

        // A report from another push of the pull request, or from another one, would give
        // confident candidates that are wrong, or a "no miss" that means nothing (#58).
        if (!string.IsNullOrWhiteSpace(settings.ExpectHead) && !SelectionMisses.SameCommit(selection.HeadCommit, settings.ExpectHead))
        {
            CliConsole.Error(selection.HeadCommit is null
                ? $"{Markup.Escape(settings.Tia)} records no head commit, so it cannot be shown to be from {Markup.Escape(settings.ExpectHead)}."
                : $"{Markup.Escape(settings.Tia)} is from {Markup.Escape(selection.HeadCommit)}, not {Markup.Escape(settings.ExpectHead)}: its selection is not the one to check against.");
            return ExitCodes.Usage;
        }

        var failures = new List<TestFailure>();
        var reports = 0;
        foreach (var (source, text) in inputs)
        {
            var detected = format == ReportFormat.Auto ? TestReportParser.Detect(source, text) : format;
            if (detected is not (ReportFormat.JUnit or ReportFormat.Trx))
            {
                // A results directory holds the runner's logs beside its reports, so one is
                // passed over rather than failing the run - but said, because a log has no test
                // names to compare.
                CliConsole.Warn($"{Markup.Escape(source)} is not a JUnit or TRX report; skipped.");
                continue;
            }

            reports++;

            try
            {
                failures.AddRange(TestReportParser.ParseFailures(detected, text));
            }
            catch (XmlException ex)
            {
                CliConsole.Error($"could not parse {Markup.Escape(source)} — {Markup.Escape(ex.Message)}");
                return ExitCodes.Usage;
            }
        }

        if (reports == 0)
        {
            // "No selection miss" from nothing read would read as a clean bill of health.
            CliConsole.Error("none of the inputs is a JUnit or TRX report, so there was nothing to check.");
            return ExitCodes.Usage;
        }

        var check = SelectionMisses.Check(selection, failures);
        if (!settings.NoHistory && check.Misses.Count > 0)
            check = check with { Misses = WithHistory(settings, check.Misses) };

        if (settings.Json)
            Console.Out.WriteLine(JsonSerializer.Serialize(ToDto(selection, failures.Count, check), AnalysisJson.Options));
        else
            Render(selection, failures.Count, check);

        // Unmatched failures do not change the exit code: none of them is known to be a miss. The
        // text and the JSON say they were not checked, so 0 is not read as a clean check.
        return check.Misses.Count == 0 ? ExitCodes.Ok : ExitCodes.Negative;
    }

    /// <summary>
    /// The misses with their earlier failures, from the store <c>analyze</c> records into (#60).
    /// Advisory: a store that can't be read is a warning, never a failed check.
    /// </summary>
    private static IReadOnlyList<SelectionMiss> WithHistory(Settings settings, IReadOnlyList<SelectionMiss> misses)
    {
        try
        {
            var git = string.IsNullOrWhiteSpace(settings.Commit) || string.IsNullOrWhiteSpace(settings.Repo)
                ? GitContext.Detect(Directory.GetCurrentDirectory())
                : null;
            using var store = StoreSupport.Open(settings);
            return SelectionMisses.WithHistory(
                misses,
                store.GetRecent(HistoryScanLimit),
                string.IsNullOrWhiteSpace(settings.Repo) ? git?.RepoId : settings.Repo,
                string.IsNullOrWhiteSpace(settings.Commit) ? git?.Head : settings.Commit);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CliConsole.Warn($"failure history was not consulted: {Markup.Escape(ex.Message)}");
            return misses;
        }
    }

    /// <summary>How many recent records the history lookup reads, as <c>stats --tests</c> does.</summary>
    private const int HistoryScanLimit = 5000;

    private static void Render(TiaSelection selection, int failureCount, SelectionCheck check)
    {
        var console = CliConsole.Out;
        var misses = check.Misses;
        var against = selection.HeadCommit is { Length: > 0 } head
            ? $" at [bold]{Markup.Escape(head[..Math.Min(9, head.Length)])}[/]"
            : string.Empty;

        if (misses.Count == 0 && check.Complete)
        {
            console.MarkupLine($"[green]No selection miss:[/] none of the {failureCount} failing test(s) was skipped by dotnet-tia{against}.");
            return;
        }

        if (misses.Count == 0)
        {
            console.MarkupLine(check.Unmatched.Count > 0
                ? $"[yellow]No selection miss found{against}, but {check.Unmatched.Count} of {failureCount} failing test(s) could not be checked.[/]"
                : $"[yellow]No selection miss found{against}, but {check.UncheckedProjects.Count} test project(s) the selection skipped whole could not be checked.[/]");
            RenderUnmatched(check.Unmatched);
            RenderUncheckedProjects(check.UncheckedProjects);
            return;
        }

        console.MarkupLine($"[red]{misses.Count} of {failureCount} failing test(s) were skipped by dotnet-tia{against}[/] — candidate selection misses:");
        console.WriteLine();
        foreach (var miss in misses)
        {
            console.MarkupLine($"  [bold]{Markup.Escape(miss.Test)}[/] [grey]({Markup.Escape(miss.Project)})[/]" +
                               (miss.History is { } history ? $" [yellow]{Markup.Escape(Before(history))}[/]" : string.Empty));
            if (miss.Failure.Message is { Length: > 0 } message)
                console.MarkupLine($"    [grey]{Markup.Escape(FirstLine(message))}[/]");
        }

        RenderUnmatched(check.Unmatched);
        RenderUncheckedProjects(check.UncheckedProjects);

        console.WriteLine();
        CliConsole.Hint("[grey]A skipped test can also fail for a reason the change had nothing to do with. " +
                        "Check out the selective run's commit and run [bold]dotnet tia explain <test>[/]: if nothing connects it to the change " +
                        "and the change broke it, that is a miss worth reporting at github.com/SebHenn/dotnet-tia/issues.[/]");
    }

    private static void RenderUnmatched(IReadOnlyList<UnmatchedFailure> unmatched)
    {
        if (unmatched.Count == 0) return;

        var console = CliConsole.Out;
        console.WriteLine();
        console.MarkupLine("[yellow]Not checked:[/] the report names these only by method, and more than one skipped test has that name:");
        foreach (var failure in unmatched)
        {
            console.MarkupLine($"  [bold]{Markup.Escape(failure.Failure.FullName)}[/] [grey]could be {Markup.Escape(string.Join(", ", failure.Candidates))}[/]");
        }
    }

    private static void RenderUncheckedProjects(IReadOnlyList<UncheckedProject> projects)
    {
        if (projects.Count == 0) return;

        var console = CliConsole.Out;
        console.WriteLine();
        console.MarkupLine("[yellow]Not checked:[/] the selection skipped these test projects whole, and a failure from them could not be recognised:");
        foreach (var project in projects)
        {
            console.MarkupLine($"  [bold]{Markup.Escape(project.Project)}[/][grey]: {Markup.Escape(project.Reason)}[/]");
        }
    }

    /// <summary><c>failed 4x on 3 days before, last 2026-10-20 - likely flaky</c>.</summary>
    internal static string Before(TestStat history) =>
        $"failed {history.FailureCount}x on {history.DistinctDays} day(s) before, last {history.LastSeen:yyyy-MM-dd}" +
        (history.Flaky ? " - flaky: it failed again after being resolved" : " - likely flaky");

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static object ToDto(TiaSelection selection, int failureCount, SelectionCheck check) => new
    {
        tia = new { selection.HeadCommit, selection.BaseRef, selection.BaseCommit },
        failingTests = failureCount,
        misses = check.Misses.Select(m => new
        {
            test = m.Test,
            project = m.Project,
            message = m.Failure.Message,
            history = m.History is { } h
                ? new { failures = h.FailureCount, distinctDays = h.DistinctDays, flaky = h.Flaky, lastSeen = h.LastSeen }
                : null,
        }).ToList(),
        unmatched = check.Unmatched.Select(u => new { test = u.Failure.FullName, candidates = u.Candidates, message = u.Failure.Message }).ToList(),
        uncheckedProjects = check.UncheckedProjects.Select(p => new { project = p.Project, reason = p.Reason }).ToList(),
    };
}
