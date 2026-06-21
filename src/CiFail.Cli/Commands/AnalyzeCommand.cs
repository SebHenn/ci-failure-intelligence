using System.ComponentModel;
using CiFail.Cli.Output;
using CiFail.Core.Ai;
using CiFail.Core.Analysis;
using CiFail.Core.Configuration;
using CiFail.Core.Git;
using CiFail.Core.Models;
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
    // Exit codes (documented for CI use).
    private const int ExitMatched = 0;   // at least one log produced a confident match
    private const int ExitNoMatch = 1;   // analyzed, but no rule matched
    private const int ExitInputError = 2; // could not read input

    public sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Log file(s) to analyze. Reads stdin when omitted.")]
        public string[] Paths { get; init; } = Array.Empty<string>();

        [CommandOption("-t|--type <ECOSYSTEM>")]
        [Description("Force ecosystem (dotnet|node|python|generic) instead of auto-detecting.")]
        public string? Type { get; init; }

        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a human report.")]
        public bool Json { get; init; }

        [CommandOption("--ai")]
        [Description("Consult an AI model (default: local Ollama) when rule confidence is low.")]
        public bool Ai { get; init; }

        [CommandOption("--ai-provider <PROVIDER>")]
        [Description("AI backend: ollama (default), anthropic, openai.")]
        public string? AiProvider { get; init; }

        [CommandOption("--ai-model <MODEL>")]
        [Description("Model name for the AI backend (overrides the provider default).")]
        public string? AiModel { get; init; }

        [CommandOption("--no-history")]
        [Description("Do not persist this analysis to history.")]
        public bool NoHistory { get; init; }

        [CommandOption("--no-git")]
        [Description("Skip git correlation (don't tag records or auto-resolve past failures).")]
        public bool NoGit { get; init; }

        [CommandOption("--top <N>")]
        [Description("Maximum number of similar past failures to show.")]
        [DefaultValue(3)]
        public int Top { get; init; } = 3;
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        List<(string source, string text)> inputs;
        try
        {
            inputs = ReadInputs(settings.Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return ExitInputError;
        }

        if (inputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]cifail needs a log to look at.[/] Two ways to give it one:");
            AnsiConsole.MarkupLine("  1. Point it at a file:   [bold]cifail analyze build.log[/]");
            AnsiConsole.MarkupLine("  2. Pipe a command's output straight in:");
            AnsiConsole.MarkupLine("       [grey]dotnet build 2>&1 | cifail analyze[/]");
            AnsiConsole.MarkupLine("       [grey]npm install   2>&1 | cifail analyze[/]");
            return ExitInputError;
        }

        var options = new AnalysisOptions
        {
            EcosystemOverride = settings.Type,
            EnableAi = settings.Ai,
            RecordHistory = !settings.NoHistory,
            TopSimilar = settings.Top,
        };

        // analyze runs the full pipeline locally (save + similarity), which the remote http
        // store doesn't serve yet — posting logs to a server lands in a later release.
        if (!string.IsNullOrWhiteSpace(settings.Server))
        {
            AnsiConsole.MarkupLine("[red]error:[/] [bold]analyze --server[/] isn't supported. " +
                "Run [bold]analyze[/] against a local/direct database; use [bold]history[/]/[bold]resolve[/]/" +
                "[bold]reconcile[/] to work against a server.");
            return ExitInputError;
        }

        // History + similarity are backed by the configured store (SQLite by default).
        // --no-history still queries similarity but skips persisting the current run.
        using var store = StoreSupport.TryCreate(settings);
        if (store is null) return ExitInputError;

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

        var service = AnalysisService.CreateWithStore(store, git, ai, embedder);

        bool allMatched = true;
        var results = new List<Analysis>(inputs.Count);
        var observed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (source, text) in inputs)
        {
            var analysis = service.Analyze(source, text, options);
            results.Add(analysis);
            observed.Add(analysis.Fingerprint.ToString());
            allMatched &= analysis.HasMatch;
        }

        if (settings.Json)
            EmitJson(results);
        else
            EmitConsole(results);

        // Failures the current run did not reproduce (and that sit on an ancestor commit)
        // are credited to the commits since — reported only in the human view.
        var autoResolved = service.ReconcileResolutions(observed);
        if (!settings.Json && autoResolved.Count > 0)
            ReportAutoResolved(autoResolved);

        return allMatched ? ExitMatched : ExitNoMatch;
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
            AnsiConsole.MarkupLine($"[yellow]warning:[/] AI disabled — {Markup.Escape(ex.Message)}");
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
                AnsiConsole.MarkupLine(
                    $"[yellow]warning:[/] the '{Markup.Escape(ai.Provider)}' AI provider has no embeddings; " +
                    "using TF-IDF similarity.");
            return embedder;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]warning:[/] embeddings disabled — {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static List<(string, string)> ReadInputs(IReadOnlyList<string> paths)
    {
        var inputs = new List<(string, string)>();

        if (paths.Count == 0)
        {
            if (!Console.IsInputRedirected)
                return inputs; // nothing piped; caller reports the usage error
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

    private static void EmitJson(IReadOnlyList<Analysis> results)
    {
        // Single input -> a single object; multiple -> an array. Keeps the common
        // case simple while staying valid for batch use.
        var payload = results.Count == 1
            ? JsonOutput.Serialize(results[0])
            : "[" + string.Join(",", results.Select(JsonOutput.Serialize)) + "]";
        Console.Out.WriteLine(payload);
    }

    private static void ReportAutoResolved(IReadOnlyList<ResolutionReconciler.Resolved> resolved)
    {
        AnsiConsole.WriteLine();
        var noun = resolved.Count == 1 ? "failure" : "failures";
        AnsiConsole.MarkupLine($"[green]✓[/] Auto-resolved {resolved.Count} past {noun} (no longer happening here):");
        foreach (var r in resolved)
        {
            var shortSha = r.Commit.Length >= 7 ? r.Commit[..7] : r.Commit;
            AnsiConsole.MarkupLine($"  [grey]#{r.Id}[/] likely fixed by [bold]{Markup.Escape(shortSha)}[/]");
        }
    }

    private static void EmitConsole(IReadOnlyList<Analysis> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0) AnsiConsole.WriteLine();
            ConsoleRenderer.Render(AnsiConsole.Console, results[i]);
        }
    }
}
