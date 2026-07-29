using System.ComponentModel;
using CiFail.Cli.Output;
using CiFail.Core.Rules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail rules validate [path]` — lint rule packs (malformed regex, missing fields,
/// duplicate ids, out-of-range confidence). Exits non-zero on any error so it can guard
/// the shipped packs in CI. With no path it checks the embedded packs + your user packs.
/// </summary>
public sealed class RulesValidateCommand : Command<RulesValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[path]")]
        [Description("Directory of *.yaml packs to validate. Omit to validate embedded + user packs.")]
        public string? Path { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        RulePackValidator.Result result;
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            result = RulePackValidator.ValidateAll();
        }
        else if (!Directory.Exists(settings.Path))
        {
            CliConsole.Error($"directory not found: {Markup.Escape(settings.Path)}");
            return ExitCodes.Usage;
        }
        else
        {
            result = RulePackValidator.ValidatePath(settings.Path);
        }

        // The diagnostics stay on stdout: for a linter they *are* the answer, and CI jobs
        // already capture them from there.
        foreach (var d in result.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Source, StringComparer.Ordinal))
        {
            var tag = d.Severity == DiagnosticSeverity.Error ? "[red]error[/]" : "[yellow]warn [/]";
            var where = d.RuleId is null
                ? Markup.Escape(d.Source)
                : $"{Markup.Escape(d.Source)} [grey]({Markup.Escape(d.RuleId)})[/]";
            CliConsole.Out.MarkupLine($"{tag} {where}: {Markup.Escape(d.Message)}");
        }

        CliConsole.Out.WriteLine();
        var summary = $"{result.RuleCount} rules {Glyphs.Dot} {result.ErrorCount} errors {Glyphs.Dot} {result.WarningCount} warnings";
        if (result.HasErrors)
        {
            CliConsole.Out.MarkupLine($"[red]{Glyphs.Cross} {summary}[/]");
            return ExitCodes.Negative;
        }

        CliConsole.Out.MarkupLine($"[green]{Glyphs.Check} {summary}[/]");
        return ExitCodes.Ok;
    }
}
