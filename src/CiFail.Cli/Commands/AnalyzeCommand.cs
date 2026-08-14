using System.ComponentModel;
using System.Net;
using System.Xml;
using CiFail.Cli.Output;
using CiFail.Core.Ai;
using CiFail.Core.Analysis;
using CiFail.Core.Configuration;
using CiFail.Core.Git;
using CiFail.Core.Ingest;
using CiFail.Core.Ingest.Reports;
using CiFail.Core.Models;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail analyze [paths...]` — the main command. Reads one or more log files (or
/// stdin when no path is given), runs the offline analysis pipeline, and renders the
/// result as a Spectre report or, with --json, a machine-readable document.
/// </summary>
public sealed class AnalyzeCommand : Command<AnalyzeCommand.Settings>
{
    // The two codes CI depends on. They keep their original meaning: 0 = every input matched a
    // rule, 1 = analyzed fine but something went unexplained. Anything actually wrong is >= 2
    // (see ExitCodes), so existing `if cifail analyze log; then` scripts are unaffected.
    private const int ExitMatched = ExitCodes.Ok;
    private const int ExitNoMatch = ExitCodes.Negative;

    public sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Log file(s) to analyze. Reads stdin when omitted.")]
        public string[] Paths { get; init; } = Array.Empty<string>();

        [CommandOption("-t|--type <ECOSYSTEM>")]
        [Description($"Force ecosystem ({EcosystemDetector.SupportedNamesText}) instead of auto-detecting.")]
        public string? Type { get; init; }

        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a human report.")]
        public bool Json { get; init; }

        [CommandOption("--format <FORMAT>")]
        [Description("Input format: auto (default), log, junit, trx. Report formats analyze each failing test.")]
        public string? Format { get; init; }

        [CommandOption("--annotations")]
        [Description("Emit GitHub Actions ::error:: annotations per failing test (when running under Actions).")]
        public bool Annotations { get; init; }

        [CommandOption("--report <FORMAT>")]
        [Description("Also produce a report: sarif (GitHub Code Scanning) or markdown (PR/step summary).")]
        public string? Report { get; init; }

        [CommandOption("--report-out <FILE>")]
        [Description("Write the --report to this file (default: stdout, which suppresses the normal view).")]
        public string? ReportOut { get; init; }

        [CommandOption("--ai")]
        [Description("Consult an AI model (default: local Ollama) when rule confidence is low.")]
        public bool Ai { get; init; }

        [CommandOption("--ai-provider <PROVIDER>")]
        [Description("AI backend: ollama (default), anthropic, openai.")]
        public string? AiProvider { get; init; }

        [CommandOption("--ai-model <MODEL>")]
        [Description("Model name for the AI backend (overrides the provider default).")]
        public string? AiModel { get; init; }

        [CommandOption("--rules <DIR>")]
        [Description("Extra directory of rule packs (repeatable). Adds to ~/.cifail/rules and .cifail/rules.")]
        public string[] Rules { get; init; } = Array.Empty<string>();

        [CommandOption("--no-history")]
        [Description("Do not persist this analysis to history.")]
        public bool NoHistory { get; init; }

        [CommandOption("--no-git")]
        [Description("Skip git correlation (don't tag records or auto-resolve past failures).")]
        public bool NoGit { get; init; }

        [CommandOption("--top <N>")]
        [Description("Maximum number of similar past failures to show.")]
        [DefaultValue(DefaultTopSimilar)]
        public int Top { get; init; } = DefaultTopSimilar;

        [CommandOption("--context <N>")]
        [Description("Log lines to show either side of the matched line (0 for just the line).")]
        [DefaultValue(AnalysisOptions.DefaultContextLines)]
        public int Context { get; init; } = AnalysisOptions.DefaultContextLines;
    }

    /// <summary>Shared by the option default and the "--top is ignored with --server" check.</summary>
    internal const int DefaultTopSimilar = 3;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Validate every flag before touching input, so a typo costs nothing and reports all
        // at once rather than after a slow read.
        if (Validate(settings) is { } invalid) return invalid;

        List<(string source, string text)> inputs;
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
            CliConsole.Hint("[yellow]cifail needs a log to look at.[/] Two ways to give it one:");
            CliConsole.Hint("  1. Point it at a file:   [bold]cifail analyze build.log[/]");
            CliConsole.Hint("  2. Pipe a command's output straight in:");
            CliConsole.Hint("       [grey]dotnet build 2>&1 | cifail analyze[/]");
            CliConsole.Hint("       [grey]npm install   2>&1 | cifail analyze[/]");
            return ExitCodes.Usage;
        }

        // R17: a raw log is one unit; a JUnit/TRX report expands into one unit per failing
        // test, so similarity/history/fingerprints work per test.
        var format = TestReportParser.TryParseFormat(settings.Format)!.Value;

        List<AnalysisUnit> units;
        try
        {
            units = AnalysisInputs.BuildUnits(inputs, format);
        }
        catch (XmlException ex)
        {
            CliConsole.Error($"could not parse the report — {Markup.Escape(ex.Message)}");
            return ExitCodes.Usage;
        }

        if (units.Count == 0)
        {
            // A report that parsed cleanly but had no failing tests is a success — but it still
            // has to produce every output that was asked for. Returning here directly meant
            // `--json` printed nothing at all (so `| jq` failed on empty input) and
            // `--report sarif --report-out` never created the file, which broke the
            // upload-sarif chain the README documents on exactly the runs where nothing is wrong.
            if (EmitResults(settings, Array.Empty<Analysis>()) is { } emptyWriteError)
                return emptyWriteError;
            return ExitMatched;
        }

        var options = new AnalysisOptions
        {
            EcosystemOverride = settings.Type,
            EnableAi = settings.Ai,
            RecordHistory = !settings.NoHistory,
            TopSimilar = settings.Top,
            ContextLines = Math.Max(0, settings.Context),
        };

        // analyze --server (R18): the server's POST /analyze runs the full pipeline (rules +
        // similarity + persistence + notifications) itself, so route there instead of the local
        // store. Git correlation is a no-op remotely (the server has no working tree, as in R11).
        if (!string.IsNullOrWhiteSpace(settings.Server))
            return AnalyzeViaServer(settings, units, options.RecordHistory);

        // History + similarity are backed by the configured store (SQLite by default).
        // --no-history still queries similarity but skips persisting the current run.
        return StoreSupport.WithStore(settings, store => AnalyzeLocally(settings, units, options, store));
    }

    /// <summary>
    /// Check the flags whose values we own before doing any work. Returns an exit code when
    /// something is wrong, null when everything is fine.
    /// </summary>
    private static int? Validate(Settings settings)
    {
        // An unrecognized --type used to be *silently ignored* and auto-detection ran instead —
        // so `--type dotnetcore` looked like it worked and quietly analyzed as something else.
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

        // R24: --report adds a SARIF/Markdown rendering.
        if (settings.Report is not null && ParseReportFormat(settings.Report) is null)
        {
            CliConsole.Error($"unknown --report '{Markup.Escape(settings.Report)}' (use sarif or markdown).");
            return ExitCodes.Usage;
        }

        // A rules directory that isn't there loads nothing, and "no rule matched" looks exactly
        // the same as "the pack never loaded" — so say so rather than letting it pass silently.
        foreach (var dir in settings.Rules.Where(d => !Directory.Exists(d)))
            CliConsole.Warn($"rules directory not found: {Markup.Escape(dir)}");

        // --server runs the whole pipeline remotely, against the server's own packs.
        if (!string.IsNullOrWhiteSpace(settings.Server))
        {
            if (settings.Rules.Length > 0)
                CliConsole.Warn("--rules is ignored with --server; the server matches with its own rule packs.");

            // These were accepted and silently dropped, which is worse than rejecting them: you
            // get a plausible-looking result produced by a different pipeline than you asked for.
            if (settings.Ai || settings.AiProvider is not null || settings.AiModel is not null)
                CliConsole.Warn("--ai is ignored with --server; the server decides whether AI runs.");

            if (settings.Top != DefaultTopSimilar)
                CliConsole.Warn("--top is ignored with --server; the server chooses how many similar failures to return.");

            if (!settings.NoGit)
                CliConsole.Hint("[grey]--server does not auto-resolve past failures; run [bold]cifail reconcile --server[/] for that.[/]");
        }

        return null;
    }

    private static int AnalyzeLocally(Settings settings, IReadOnlyList<AnalysisUnit> units,
        AnalysisOptions options, IAnalysisStore store)
    {
        // Git correlation (R3): when run inside a repo, tag each record with the commit and
        // auto-resolve past failures that no longer occur at HEAD.
        var git = settings.NoGit ? null : GitContext.Detect(Directory.GetCurrentDirectory());

        // Optional AI (R8): build the configured analyzer only when --ai is set. A
        // misconfiguration (unknown provider / missing key) warns and proceeds rules-only.
        // Optional embeddings (R10): built when opted in via config (ai.embeddings); only useful
        // with a vector-capable store (pgvector), otherwise harmless.
        var aiConfig = ResolveAiConfig(settings);
        var ai = settings.Ai ? BuildAiAnalyzer(aiConfig) : null;
        var embedder = BuildEmbedder(aiConfig);

        var service = AnalysisService.CreateWithStore(store, git, ai, embedder, settings.Rules);

        bool allMatched = true;
        var results = new List<Analysis>(units.Count);
        var observed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            var analysis = service.Analyze(unit.Source, unit.Text, options);
            results.Add(analysis);
            observed.Add(analysis.Fingerprint.ToString());
            allMatched &= analysis.HasMatch;
        }

        RuleDiagnostics.Report(results);
        ReportDetectionDetail(units, results);

        if (EmitResults(settings, results) is { } writeError) return writeError;

        // GitHub annotations (R17): surface each failing test inline on the PR.
        if (settings.Annotations)
            EmitAnnotations(units, results);

        // Failures the current run did not reproduce (and that sit on an ancestor commit)
        // are credited to the commits since — reported only in the human view.
        var autoResolved = service.ReconcileResolutions(observed);
        if (!settings.Json && autoResolved.Count > 0)
            ReportAutoResolved(autoResolved);

        return allMatched ? ExitMatched : ExitNoMatch;
    }

    /// <summary>
    /// Under <c>--verbose</c>, show why each ecosystem was chosen.
    ///
    /// <para>
    /// <c>EcosystemDetector.Rank</c> is public and documented as the answer to "why did it think
    /// this was Java?" — and until now nothing but its own tests ever called it. Detection being
    /// wrong is a common cause of "no rule matched", because the wrong ecosystem *narrows* the
    /// rule set rather than widening it, so the scores are the first thing worth seeing.
    /// </para>
    /// </summary>
    private static void ReportDetectionDetail(
        IReadOnlyList<AnalysisUnit> units, IReadOnlyList<Analysis> results)
    {
        if (!CliConsole.Verbose) return;

        for (var i = 0; i < results.Count && i < units.Count; i++)
        {
            var log = LogNormalizer.Build(units[i].Source, units[i].Text);
            var ranked = EcosystemDetector.Rank(log)
                .Where(r => r.Score > 0)
                .Take(4)
                .Select(r => $"{r.Ecosystem.ToString().ToLowerInvariant()} {r.Score}");

            var scores = string.Join(", ", ranked);
            CliConsole.Detail(
                $"[grey]{Markup.Escape(units[i].Source)}: detected " +
                $"[bold]{results[i].Ecosystem.ToString().ToLowerInvariant()}[/]" +
                (string.IsNullOrEmpty(scores) ? " (no markers matched)" : $" from {Markup.Escape(scores)}") +
                "[/]");
        }
    }

    private static AiConfig ResolveAiConfig(Settings settings)
    {
        var ai = ConfigLoader.Load().Ai;
        if (!string.IsNullOrWhiteSpace(settings.AiProvider)) ai.Provider = settings.AiProvider.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(settings.AiModel)) ai.Model = settings.AiModel;
        return ai;
    }

    private static IAiAnalyzer? BuildAiAnalyzer(AiConfig ai)
    {
        try
        {
            return AiFactory.Create(ai);
        }
        catch (Exception ex)
        {
            // Don't fail the analysis just because AI is misconfigured — warn and go rules-only.
            CliConsole.Warn($"AI disabled — {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static IAiEmbedder? BuildEmbedder(AiConfig ai)
    {
        if (!ai.Embeddings) return null;
        try
        {
            var embedder = AiFactory.CreateEmbedder(ai);
            if (embedder is null)
                CliConsole.Warn($"the '{Markup.Escape(ai.Provider)}' AI provider has no embeddings; " +
                    "using TF-IDF similarity.");
            return embedder;
        }
        catch (Exception ex)
        {
            CliConsole.Warn($"embeddings disabled — {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    /// <summary>One thing to analyze: a raw log, or a single failing test from a report.</summary>
    /// <summary>
    /// Emit a GitHub Actions <c>::error::</c> annotation per failing test, so failures show
    /// inline on the PR. Only fires under Actions (GITHUB_ACTIONS=true); a no-op elsewhere.
    ///
    /// <para>
    /// Written to <b>stderr</b>. The runner scans both streams for workflow commands, so the
    /// annotations still appear — while <c>--annotations --json</c> no longer interleaves
    /// <c>::error::</c> lines into the JSON document on stdout and breaks the parse.
    /// </para>
    /// </summary>
    private static void EmitAnnotations(IReadOnlyList<AnalysisUnit> units, IReadOnlyList<Analysis> results)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].Test is not { } test) continue;
            var analysis = results[i];
            var message = analysis.RootCause is { } rc ? $"{rc.Rule.Title} — {rc.Fix}" : test.Message;
            Console.Error.WriteLine(
                $"::error title={EscapeAnnotationProperty(test.FullName)}::{EscapeAnnotationData(message)}");
        }
    }

    // GitHub workflow-command escaping (https://docs.github.com/actions ... workflow-commands).
    private static string EscapeAnnotationData(string s) =>
        s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string EscapeAnnotationProperty(string s) =>
        EscapeAnnotationData(s).Replace(":", "%3A").Replace(",", "%2C");

    /// <summary>
    /// Analyze each unit against a remote <c>cifail serve</c> via <c>POST /analyze</c>, then render
    /// with the same console/--json path as a local run. The token comes from --server-token or
    /// the env var, matching the other remote commands.
    /// </summary>
    private static int AnalyzeViaServer(Settings settings, IReadOnlyList<AnalysisUnit> units, bool recordHistory)
    {
        var results = new List<Analysis>(units.Count);
        try
        {
            using var client = new HttpAnalyzeClient(settings.Server!, StoreSupport.ResolveToken(settings));
            foreach (var unit in units)
                results.Add(client.Analyze(unit.Text, settings.Type, unit.Source, noHistory: !recordHistory));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            CliConsole.Error($"{Markup.Escape(settings.Server!)} rejected the request (401 Unauthorized).");
            CliConsole.Hint($"[grey]Pass [bold]--server-token <TOKEN>[/] or set " +
                $"[bold]{HttpAnalysisStore.TokenEnvVar}[/].[/]");
            return ExitCodes.StoreUnavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or UriFormatException)
        {
            CliConsole.Error($"could not analyze against {Markup.Escape(settings.Server!)} — {Markup.Escape(ex.Message)}");
            return ExitCodes.StoreUnavailable;
        }

        if (EmitResults(settings, results) is { } writeError) return writeError;

        if (settings.Annotations)
            EmitAnnotations(units, results);

        return results.All(r => r.HasMatch) ? ExitMatched : ExitNoMatch;
    }

    /// <summary>
    /// Render results: the normal console/--json view, plus an optional SARIF/Markdown report (R24).
    /// When --report goes to stdout (no --report-out) it takes over stdout, so the normal view is
    /// suppressed; with --report-out the report is written to the file and the normal view still shows.
    /// </summary>
    /// <returns>An exit code if writing the report failed, otherwise null.</returns>
    private static int? EmitResults(Settings settings, IReadOnlyList<Analysis> results)
    {
        var reportToStdout = settings.Report is not null && string.IsNullOrWhiteSpace(settings.ReportOut);

        if (!reportToStdout)
        {
            if (settings.Json) EmitJson(results);
            else EmitConsole(results);
        }

        if (settings.Report is null) return null;

        var dtos = results.Select(AnalysisJson.ToDto).ToList();
        var content = ParseReportFormat(settings.Report) switch
        {
            ReportKind.Sarif => SarifOutput.Build(dtos),
            _ => MarkdownOutput.Build(dtos),
        };

        if (string.IsNullOrWhiteSpace(settings.ReportOut))
        {
            Console.Out.Write(content);
            return null;
        }

        try
        {
            // A missing directory or a read-only path is a user mistake, not a crash: an
            // unwritable --report-out used to surface as an unhandled IOException.
            // Create the parent directory as `gate --update` does — `--report-out out/x.sarif`
            // is the natural thing to write in CI, and failing because `out/` doesn't exist yet
            // is a papercut, not a safety property.
            var directory = Path.GetDirectoryName(Path.GetFullPath(settings.ReportOut));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(settings.ReportOut, content);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            CliConsole.Error($"could not write the report to " +
                $"{Markup.Escape(settings.ReportOut)} — {Markup.Escape(ex.Message)}");
            return ExitCodes.Usage;
        }
    }

    private enum ReportKind { Sarif, Markdown }

    private static ReportKind? ParseReportFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sarif" => ReportKind.Sarif,
        "markdown" or "md" => ReportKind.Markdown,
        _ => null,
    };

    /// <summary>
    /// Always a JSON <b>array</b>, one element per analysis unit — including the empty array when
    /// nothing failed.
    ///
    /// <para>
    /// This used to emit a bare object for a single input and an array for several, which meant
    /// the shape of the document depended on how many files a glob happened to match:
    /// <c>cifail analyze *.log --json | jq '.[0]'</c> worked until the day one log was left, and
    /// a clean test report produced no document at all. A consumer cannot branch on that. The
    /// change is breaking and is called out in CHANGELOG.md.
    /// </para>
    /// </summary>
    private static void EmitJson(IReadOnlyList<Analysis> results)
    {
        Console.Out.WriteLine("[" + string.Join(",", results.Select(JsonOutput.Serialize)) + "]");
    }

    private static void ReportAutoResolved(IReadOnlyList<ResolutionReconciler.Resolved> resolved)
    {
        CliConsole.Out.WriteLine();
        var noun = resolved.Count == 1 ? "failure" : "failures";
        CliConsole.Out.MarkupLine($"[green]{Glyphs.Check}[/] Auto-resolved {resolved.Count} past {noun} (no longer happening here):");
        foreach (var r in resolved)
        {
            var shortSha = r.Commit.Length >= 7 ? r.Commit[..7] : r.Commit;
            CliConsole.Out.MarkupLine($"  [grey]#{r.Id}[/] likely fixed by [bold]{Markup.Escape(shortSha)}[/]");
        }
    }

    private static void EmitConsole(IReadOnlyList<Analysis> results)
    {
        // Only reachable from a structured report that parsed cleanly and had no failing tests.
        if (results.Count == 0)
        {
            CliConsole.Out.MarkupLine($"[green]{Glyphs.Check}[/] No failing tests found.");
            return;
        }

        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0) CliConsole.Out.WriteLine();
            ConsoleRenderer.Render(CliConsole.Out, results[i]);
        }
    }
}
