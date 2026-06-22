using System.ComponentModel;
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
            AnsiConsole.MarkupLine($"[red]error:[/] directory not found: {Markup.Escape(settings.Path)}");
            return 2;
        }
        else
        {
            result = RulePackValidator.ValidatePath(settings.Path);
        }

        foreach (var d in result.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Source, StringComparer.Ordinal))
        {
            var tag = d.Severity == DiagnosticSeverity.Error ? "[red]error[/]" : "[yellow]warn [/]";
            var where = d.RuleId is null
                ? Markup.Escape(d.Source)
                : $"{Markup.Escape(d.Source)} [grey]({Markup.Escape(d.RuleId)})[/]";
            AnsiConsole.MarkupLine($"{tag} {where}: {Markup.Escape(d.Message)}");
        }

        AnsiConsole.WriteLine();
        var summary = $"{result.RuleCount} rules · {result.ErrorCount} errors · {result.WarningCount} warnings";
        if (result.HasErrors)
        {
            AnsiConsole.MarkupLine($"[red]✗ {summary}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]✓ {summary}[/]");
        return 0;
    }
}
