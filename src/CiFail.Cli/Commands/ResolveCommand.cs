using System.ComponentModel;
using CiFail.Cli.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail resolve &lt;id&gt; --note "..."` — record how a past failure was fixed, so
/// future similar failures surface the resolution.
/// </summary>
public sealed class ResolveCommand : Command<ResolveCommand.Settings>
{
    public sealed class Settings : StoreSettings
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
        => StoreSupport.WithStore(settings, store =>
        {
            if (!store.SetResolution(settings.Id, settings.Note!.Trim()))
            {
                CliConsole.Error($"no analysis with id {settings.Id}.");
                CliConsole.Hint("[grey]Run [bold]cifail history[/] to see the ids you have.[/]");
                return ExitCodes.NotFound;
            }

            CliConsole.Out.MarkupLine(
                $"[green]{Glyphs.Check}[/] recorded resolution for analysis #{settings.Id}.");
            return ExitCodes.Ok;
        });
}
