using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using CiFail.Cli.Output;
using CiFail.Core.Analysis;
using CiFail.Core.Ingest;
using CiFail.Core.Ingest.Reports;
using CiFail.Core.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail gate` — fail CI on a <b>new</b> failure while tolerating the known backlog.
///
/// <para>
/// The difference from <c>analyze</c> is what it's for: analyze explains, gate decides. A repo
/// with fifty pre-existing failures can adopt gate on day one, baseline them, and from then on
/// only a fifty-first breaks the build.
/// </para>
///
/// <para>
/// Deliberately <b>storeless</b>: no history database, no git, no network. Its entire memory is
/// the committed baseline file. That is what lets it give the same verdict on a laptop and in a
/// scratch container with a read-only home — a gate that can fail for reasons unrelated to the
/// code under test is a gate people turn off.
/// </para>
/// </summary>
public sealed class GateCommand : Command<GateCommand.Settings>
{
    private const int ExitPassed = ExitCodes.Ok;
    private const int ExitNewFailures = ExitCodes.Negative;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Log file(s) or test report(s) to check. Reads stdin when omitted.")]
        public string[] Paths { get; init; } = Array.Empty<string>();

        [CommandOption("-b|--baseline <FILE>")]
        [Description("Baseline of accepted failures. Default: .cifail/baseline.txt")]
        public string? Baseline { get; init; }

        [CommandOption("-u|--update")]
        [Description("Rewrite the baseline from this run instead of checking against it.")]
        public bool Update { get; init; }

        [CommandOption("-t|--type <ECOSYSTEM>")]
        [Description($"Force ecosystem ({EcosystemDetector.SupportedNamesText}) instead of auto-detecting.")]
        public string? Type { get; init; }

        [CommandOption("--format <FORMAT>")]
        [Description("Input format: auto (default), log, junit, trx.")]
        public string? Format { get; init; }

        [CommandOption("--rules <DIR>")]
        [Description("Extra directory of rule packs (repeatable). Adds to ~/.cifail/rules and .cifail/rules.")]
        public string[] Rules { get; init; } = Array.Empty<string>();

        [CommandOption("--json")]
        [Description("Emit the verdict as JSON instead of a human report.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (Validate(settings) is { } invalid) return invalid;

        var baselinePath = settings.Baseline ?? GateBaseline.DefaultPath;

        List<(string Source, string Text)> inputs;
        try
        {
            inputs = AnalysisInputs.Read(settings.Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliConsole.Error(Markup.Escape(ex.Message));
            return ExitCodes.Usage;
        }

        if (inputs.Count == 0)
        {
            CliConsole.Error("gate needs a log or test report to check.");
            CliConsole.Hint("  [bold]cifail gate build.log[/]");
            CliConsole.Hint("  [grey]dotnet test --logger trx 2>&1 | cifail gate[/]");
            return ExitCodes.Usage;
        }

        List<AnalysisUnit> units;
        try
        {
            units = AnalysisInputs.BuildUnits(inputs, TestReportParser.TryParseFormat(settings.Format)!.Value);
        }
        catch (XmlException ex)
        {
            CliConsole.Error($"could not parse the report — {Markup.Escape(ex.Message)}");
            return ExitCodes.Usage;
        }

        var observed = Observe(units, settings.Type, settings.Rules);

        if (settings.Update)
            return WriteBaseline(settings, baselinePath, observed);

        string? existing;
        try
        {
            existing = File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliConsole.Error($"could not read {Markup.Escape(baselinePath)} — {Markup.Escape(ex.Message)}");
            return ExitCodes.Usage;
        }

        var result = GateComputer.Evaluate(observed, GateBaseline.Parse(existing));

        if (settings.Json)
            Console.Out.WriteLine(JsonSerializer.Serialize(GateJson.ToDto(result, baselinePath), AnalysisJson.Options));
        else
            Report(result, baselinePath, existing is null);

        return result.Passed ? ExitPassed : ExitNewFailures;
    }

    private static int? Validate(Settings settings)
    {
        if (settings.Type is not null && !EcosystemDetector.TryParse(settings.Type, out _))
        {
            CliConsole.Error($"unknown --type '{Markup.Escape(settings.Type)}'.");
            CliConsole.Hint($"[grey]Valid values: {EcosystemDetector.SupportedNamesText.Replace("|", ", ")}.[/]");
            return ExitCodes.Usage;
        }

        if (TestReportParser.TryParseFormat(settings.Format) is null)
        {
            CliConsole.Error($"unknown --format '{Markup.Escape(settings.Format!)}' (use auto, log, junit, or trx).");
            return ExitCodes.Usage;
        }

        // A missing pack changes the fingerprints, and so the verdict — worth saying out loud.
        foreach (var dir in settings.Rules.Where(d => !Directory.Exists(d)))
            CliConsole.Warn($"rules directory not found: {Markup.Escape(dir)}");

        return null;
    }

    /// <summary>
    /// Run the rules over every unit and reduce each result to its fingerprint. Rules only —
    /// see the class remarks for why the gate never opens a store.
    /// </summary>
    private static List<ObservedFailure> Observe(IReadOnlyList<AnalysisUnit> units, string? ecosystemOverride,
        IReadOnlyList<string> ruleDirs)
    {
        var service = AnalysisService.CreateDefault(ruleDirs);
        var options = new AnalysisOptions { EcosystemOverride = ecosystemOverride };

        var observed = new List<ObservedFailure>(units.Count);
        var analyses = new List<Core.Models.Analysis>(units.Count);
        foreach (var unit in units)
        {
            var analysis = service.Analyze(unit.Source, unit.Text, options);
            analyses.Add(analysis);
            observed.Add(new ObservedFailure(
                analysis.Fingerprint.ToString(),
                unit.Source,
                analysis.Fingerprint.RuleId,
                analysis.RootCause?.Rule.Title));
        }

        // A rule that was skipped is a rule that cannot gate — say so, or a pack that stopped
        // working looks exactly like a clean run.
        RuleDiagnostics.Report(analyses);

        return observed;
    }

    private static int WriteBaseline(Settings settings, string baselinePath, IReadOnlyList<ObservedFailure> observed)
    {
        // Evaluating against an empty baseline is how we get the grouped-and-deduped findings
        // that Render wants; "everything is new" is exactly right when rewriting from scratch.
        var all = GateComputer.Evaluate(observed, new HashSet<string>(StringComparer.Ordinal)).New;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(baselinePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(baselinePath, GateBaseline.Render(all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            CliConsole.Error($"could not write {Markup.Escape(baselinePath)} — {Markup.Escape(ex.Message)}");
            return ExitCodes.Usage;
        }

        if (settings.Json)
        {
            var dto = GateJson.ToDto(new GateResult(Array.Empty<GateFinding>(), all, Array.Empty<string>()), baselinePath);
            Console.Out.WriteLine(JsonSerializer.Serialize(dto, AnalysisJson.Options));
            return ExitPassed;
        }

        var noun = all.Count == 1 ? "failure" : "failures";
        CliConsole.Out.MarkupLine(
            $"[green]{Glyphs.Check}[/] Wrote {all.Count} accepted {noun} to [bold]{Markup.Escape(baselinePath)}[/].");
        CliConsole.Hint("[grey]Commit it — it's the gate's memory, and it has to be the same for everyone.[/]");
        return ExitPassed;
    }

    private static void Report(GateResult result, string baselinePath, bool baselineMissing)
    {
        var console = CliConsole.Out;

        if (result.Passed)
        {
            var known = result.Known.Count;
            console.MarkupLine(known == 0
                ? $"[green]{Glyphs.Check}[/] No failures found."
                : $"[green]{Glyphs.Check}[/] No new failures. {known} known {(known == 1 ? "failure is" : "failures are")} in the baseline.");
        }
        else
        {
            var count = result.New.Count;
            console.MarkupLine(
                $"[red]{Glyphs.Cross}[/] {count} new {(count == 1 ? "failure" : "failures")} not in the baseline:");
            console.WriteLine();

            foreach (var finding in result.New)
            {
                var title = string.IsNullOrWhiteSpace(finding.Title)
                    ? "[grey]no rule matched this one[/]"
                    : $"[bold]{Markup.Escape(finding.Title)}[/]";
                console.MarkupLine($"  {Glyphs.Bullet} {title}");
                console.MarkupLine($"    [grey]{Markup.Escape(finding.Fingerprint)}[/]");
                foreach (var source in finding.Sources.Take(3))
                    console.MarkupLine($"    [grey]in {Markup.Escape(source)}[/]");
                if (finding.Sources.Count > 3)
                    console.MarkupLine($"    [grey]{Glyphs.Ellipsis} and {finding.Sources.Count - 3} more[/]");
            }

            console.WriteLine();
            if (baselineMissing)
            {
                CliConsole.Hint($"[grey]No baseline at [bold]{Markup.Escape(baselinePath)}[/] yet, so every failure counts as new.[/]");
                CliConsole.Hint("[grey]Accept the current backlog with [bold]cifail gate --update <logs>[/], then commit it.[/]");
            }
            else
            {
                CliConsole.Hint("[grey]Fix them, or accept them with [bold]cifail gate --update <logs>[/] and commit the baseline.[/]");
            }
        }

        // Advisory only: a stale entry never affects the verdict, it just means the baseline
        // is carrying weight it no longer needs.
        if (result.Stale.Count > 0 && result.Passed)
        {
            var stale = result.Stale.Count;
            CliConsole.Hint($"[grey]{stale} baseline {(stale == 1 ? "entry" : "entries")} did not occur in this run " +
                $"{Glyphs.Dash} [bold]--update[/] would drop {(stale == 1 ? "it" : "them")}.[/]");
        }
    }
}
