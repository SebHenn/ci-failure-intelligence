using System.ComponentModel;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail resolve &lt;id&gt; --note "..."` — record how a past failure was fixed, so
/// future similar failures surface the resolution.
/// </summary>
public sealed class ResolveCommand : Command<ResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("The analysis id to annotate (see `cifail history`).")]
        public long Id { get; init; }

        [CommandOption("-m|--note <TEXT>")]
        [Description("How the failure was resolved.")]
        public string? Note { get; init; }
    }

    protected override ValidationResult Validate(CommandContext context, Settings settings)
        => string.IsNullOrWhiteSpace(settings.Note)
            ? ValidationResult.Error("a resolution note is required (--note \"...\").")
            : ValidationResult.Success();

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var store = new SqliteAnalysisRepository();

        if (!store.SetResolution(settings.Id, settings.Note!.Trim()))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no analysis with id {settings.Id}.");
            return 2;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] recorded resolution for analysis #{settings.Id}.");
        return 0;
    }
}
