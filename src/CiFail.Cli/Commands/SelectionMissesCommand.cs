using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using CiFail.Cli.Output;
using CiFail.Core.Analysis;
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
    public sealed class Settings : OutputSettings
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

        var misses = SelectionMisses.Find(selection, failures);

        if (settings.Json)
            Console.Out.WriteLine(JsonSerializer.Serialize(ToDto(selection, failures.Count, misses), AnalysisJson.Options));
        else
            Render(selection, failures.Count, misses);

        return misses.Count == 0 ? ExitCodes.Ok : ExitCodes.Negative;
    }

    private static void Render(TiaSelection selection, int failureCount, IReadOnlyList<SelectionMiss> misses)
    {
        var console = CliConsole.Out;
        var against = selection.HeadCommit is { Length: > 0 } head
            ? $" at [bold]{Markup.Escape(head[..Math.Min(9, head.Length)])}[/]"
            : string.Empty;

        if (misses.Count == 0)
        {
            console.MarkupLine($"[green]No selection miss:[/] none of the {failureCount} failing test(s) was skipped by dotnet-tia{against}.");
            return;
        }

        console.MarkupLine($"[red]{misses.Count} of {failureCount} failing test(s) were skipped by dotnet-tia{against}[/] — candidate selection misses:");
        console.WriteLine();
        foreach (var miss in misses)
        {
            console.MarkupLine($"  [bold]{Markup.Escape(miss.Test)}[/] [grey]({Markup.Escape(miss.Project)})[/]");
            if (miss.Failure.Message is { Length: > 0 } message)
                console.MarkupLine($"    [grey]{Markup.Escape(FirstLine(message))}[/]");
        }

        console.WriteLine();
        CliConsole.Hint("[grey]A skipped test can also fail for a reason the change had nothing to do with. " +
                        "Check out the selective run's commit and run [bold]dotnet tia explain <test>[/]: if nothing connects it to the change " +
                        "and the change broke it, that is a miss worth reporting at github.com/SebHenn/dotnet-tia/issues.[/]");
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static object ToDto(TiaSelection selection, int failureCount, IReadOnlyList<SelectionMiss> misses) => new
    {
        tia = new { selection.HeadCommit, selection.BaseRef, selection.BaseCommit },
        failingTests = failureCount,
        misses = misses.Select(m => new { test = m.Test, project = m.Project, message = m.Failure.Message }).ToList(),
    };
}
