using System.ComponentModel;
using System.Text.Json;
using CiFail.Core;
using CiFail.Core.Ai;
using CiFail.Core.Configuration;
using CiFail.Core.Ingest;
using CiFail.Core.Models;
using CiFail.Core.Rules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail suggest-rule [path]` — when a log isn't matched by any rule, ask the AI to draft one
/// (id/match/fix), validate it locally (it must compile, actually match the log, and not be
/// overbroad), preview the YAML, and with --write append it to the user's rule pack. The AI only
/// drafts; cifail's own validators are the gate. Fully optional and offline-degrading.
/// </summary>
public sealed class SuggestRuleCommand : Command<SuggestRuleCommand.Settings>
{
    private const double LowConfidenceThreshold = 0.6;
    private const int ExitOk = 0;
    private const int ExitNoDraft = 1;
    private const int ExitInputError = 2;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[paths]")]
        [Description("Log file to draft a rule for. Reads stdin when omitted.")]
        public string[] Paths { get; init; } = Array.Empty<string>();

        [CommandOption("-t|--type <ECOSYSTEM>")]
        [Description("Force ecosystem instead of auto-detecting.")]
        public string? Type { get; init; }

        [CommandOption("--ai-provider <PROVIDER>")]
        [Description("AI backend: ollama (default), anthropic, openai.")]
        public string? AiProvider { get; init; }

        [CommandOption("--ai-model <MODEL>")]
        [Description("Model name for the AI backend (overrides the provider default).")]
        public string? AiModel { get; init; }

        [CommandOption("--write")]
        [Description("Append the validated rule to ~/.cifail/rules/suggested.yaml.")]
        public bool Write { get; init; }

        [CommandOption("--force")]
        [Description("Write even if a rule with the same id already exists.")]
        public bool Force { get; init; }

        [CommandOption("--json")]
        [Description("Emit the draft and diagnostics as JSON instead of a human report.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        (string Source, string Text)? input;
        try
        {
            input = ReadOneInput(settings.Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return ExitInputError;
        }

        if (input is null)
        {
            AnsiConsole.MarkupLine("[yellow]cifail suggest-rule needs a log to look at.[/]");
            AnsiConsole.MarkupLine("  [bold]cifail suggest-rule build.log[/]   or   [grey]some-command 2>&1 | cifail suggest-rule[/]");
            return ExitInputError;
        }

        var (source, text) = input.Value;
        var log = LogNormalizer.Build(source, text);
        var ecosystem = EcosystemDetector.Detect(log, settings.Type);

        // Only draft when the existing rules don't already explain it confidently.
        var engine = new RuleEngine(RulePackLoader.LoadAll());
        var matches = engine.Match(log, ecosystem);
        var top = matches.Count > 0 ? matches[0] : null;
        if (top is not null && top.Score >= LowConfidenceThreshold && !settings.Json)
        {
            AnsiConsole.MarkupLine(
                $"[green]Already covered.[/] '{Markup.Escape(top.Rule.Title)}' " +
                $"([grey]{Markup.Escape(top.Rule.Id)}[/]) already matches this log with " +
                $"{Confidence(top.Score)} confidence — no new rule needed.");
            return ExitOk;
        }

        // Build the drafter (offline-degrading: a clear message, never a crash).
        var ai = ResolveAiConfig(settings);
        IAiRuleDrafter? drafter;
        try
        {
            drafter = AiFactory.CreateDrafter(ai);
        }
        catch (Exception ex)
        {
            return AiUnavailable(ai.Provider, ex.Message);
        }
        if (drafter is null)
            return AiUnavailable(ai.Provider, $"the '{ai.Provider}' provider can't draft rules yet (only local Ollama does)");

        AiRuleDraft? draft;
        try
        {
            draft = drafter.DraftRule(new AiRuleDraftRequest
            {
                LogExcerpt = Excerpt(log.NormalizedText),
                Ecosystem = ecosystem,
                WeakMatch = top,
            });
        }
        catch (Exception ex)
        {
            return AiUnavailable(ai.Provider, ex.Message);
        }

        if (draft is null)
        {
            AnsiConsole.MarkupLine("[yellow]The model didn't return a usable rule draft.[/] " +
                "Try again, or author one by hand (see CONTRIBUTING.md).");
            return ExitNoDraft;
        }

        var validation = RuleDraftValidator.Validate(draft, log);
        var yaml = RulePackLoader.Serialize(new[] { validation.Rule });

        if (settings.Json)
        {
            EmitJson(validation, yaml);
            return validation.Ok ? ExitOk : ExitNoDraft;
        }

        RenderPreview(validation, yaml);

        if (!validation.Ok)
        {
            AnsiConsole.MarkupLine("[red]Not usable as-is.[/] Fix the problems above by hand, or refine and re-run.");
            return ExitNoDraft;
        }

        if (!settings.Write)
        {
            AnsiConsole.MarkupLine("[grey]Preview only.[/] Re-run with [bold]--write[/] to save it to your user rules.");
            return ExitOk;
        }

        var clash = RulePackLoader.LoadAll()
            .Any(r => string.Equals(r.Id, validation.Rule.Id, StringComparison.OrdinalIgnoreCase));
        if (clash && !settings.Force)
        {
            AnsiConsole.MarkupLine($"[red]A rule with id '{Markup.Escape(validation.Rule.Id)}' already exists.[/] " +
                "Pass [bold]--force[/] to write it anyway (it will override that id).");
            return ExitNoDraft;
        }

        try
        {
            AppendToSuggested(validation.Rule);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] couldn't write the rule — {Markup.Escape(ex.Message)}");
            return ExitInputError;
        }

        AnsiConsole.MarkupLine($"[green]✓ Saved[/] to [bold]{Markup.Escape(CiFailPaths.SuggestedRulesPath)}[/] " +
            "— it's now part of your rules. Double-check it with [bold]cifail rules validate[/].");
        return ExitOk;
    }

    private static AiConfig ResolveAiConfig(Settings settings)
    {
        var ai = ConfigLoader.Load().Ai;
        if (!string.IsNullOrWhiteSpace(settings.AiProvider)) ai.Provider = settings.AiProvider.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(settings.AiModel)) ai.Model = settings.AiModel;
        return ai;
    }

    private static int AiUnavailable(string provider, string detail)
    {
        AnsiConsole.MarkupLine($"[yellow]AI isn't available for drafting[/] ({Markup.Escape(detail)}).");
        AnsiConsole.MarkupLine("suggest-rule needs a local model. Start [bold]Ollama[/] " +
            "([grey]https://ollama.com[/]) and pull a model, or author the rule by hand (see CONTRIBUTING.md).");
        return ExitNoDraft;
    }

    private static void RenderPreview(DraftValidation v, string yaml)
    {
        AnsiConsole.Write(new Panel(new Markup($"[grey]{Markup.Escape(yaml.TrimEnd())}[/]"))
            .Header("[bold]Proposed rule[/]")
            .Border(BoxBorder.Rounded));

        if (!string.IsNullOrWhiteSpace(v.MatchedLine))
            AnsiConsole.MarkupLine($"[grey]Matches:[/] {Markup.Escape(v.MatchedLine)}");

        foreach (var d in v.Diagnostics.OrderByDescending(d => d.Severity == DiagnosticSeverity.Error))
        {
            var (tag, colour) = d.Severity == DiagnosticSeverity.Error ? ("error", "red") : ("warning", "yellow");
            AnsiConsole.MarkupLine($"[{colour}]{tag}:[/] {Markup.Escape(d.Message)}");
        }
    }

    private static void EmitJson(DraftValidation v, string yaml)
    {
        var payload = new
        {
            Ok = v.Ok,
            v.Rule.Id,
            v.Rule.Ecosystem,
            v.Rule.Category,
            v.Rule.Title,
            v.Rule.Match,
            v.Rule.Confidence,
            v.Rule.Fix,
            v.Rule.Docs,
            MatchedLine = v.MatchedLine,
            Yaml = yaml,
            Diagnostics = v.Diagnostics.Select(d => new { Severity = d.Severity.ToString(), d.Message }).ToList(),
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AppendToSuggested(RuleDefinition rule)
    {
        Directory.CreateDirectory(CiFailPaths.UserRulesDir);
        var path = CiFailPaths.SuggestedRulesPath;

        var rules = File.Exists(path)
            ? RulePackLoader.DeserializeRaw(File.ReadAllText(path)).ToList()
            : new List<RuleDefinition>();

        // Replace any existing entry with the same id so the file stays a clean, valid sequence.
        rules.RemoveAll(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        rules.Add(rule);

        File.WriteAllText(path, RulePackLoader.Serialize(rules));
    }

    /// <summary>The tail of the log is where the failure usually is; cap what we send to the model.</summary>
    private static string Excerpt(string text) =>
        text.Length <= 6000 ? text : text[^6000..];

    private static string Confidence(double score) => score switch
    {
        >= 0.8 => "high",
        >= 0.6 => "medium",
        _ => "low",
    };

    private static (string Source, string Text)? ReadOneInput(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            if (!Console.IsInputRedirected) return null;
            var stdin = Console.In.ReadToEnd();
            return string.IsNullOrWhiteSpace(stdin) ? null : ("stdin", stdin);
        }

        var path = paths[0];
        if (!File.Exists(path))
            throw new FileNotFoundException($"file not found: {path}");
        return (path, File.ReadAllText(path));
    }
}
