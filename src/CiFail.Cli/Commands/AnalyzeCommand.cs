using System.ComponentModel;
using CiFail.Cli.Output;
using CiFail.Core.Analysis;
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

    public sealed class Settings : CommandSettings
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
        [Description("Consult a local Ollama model when rule confidence is low.")]
        public bool Ai { get; init; }

        [CommandOption("--no-history")]
        [Description("Do not persist this analysis to history.")]
        public bool NoHistory { get; init; }

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
            AnsiConsole.MarkupLine(
                "[red]error:[/] no input. Pass a log file path, or pipe a log via stdin " +
                "(e.g. [grey]dotnet build | cifail analyze[/]).");
            return ExitInputError;
        }

        var options = new AnalysisOptions
        {
            EcosystemOverride = settings.Type,
            EnableAi = settings.Ai,
            RecordHistory = !settings.NoHistory,
            TopSimilar = settings.Top,
        };

        // History + similarity are backed by the local SQLite store. Opening it
        // creates ~/.cifail/history.db on first use; --no-history still queries
        // similarity but skips persisting the current run.
        using var store = new SqliteAnalysisRepository();
        var service = AnalysisService.CreateWithStore(store);

        bool allMatched = true;
        var results = new List<Analysis>(inputs.Count);

        foreach (var (source, text) in inputs)
        {
            var analysis = service.Analyze(source, text, options);
            results.Add(analysis);
            allMatched &= analysis.HasMatch;
        }

        if (settings.Json)
            EmitJson(results);
        else
            EmitConsole(results);

        return allMatched ? ExitMatched : ExitNoMatch;
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

    private static void EmitConsole(IReadOnlyList<Analysis> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0) AnsiConsole.WriteLine();
            ConsoleRenderer.Render(AnsiConsole.Console, results[i]);
        }
    }
}
