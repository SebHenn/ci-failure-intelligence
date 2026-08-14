using System.ComponentModel;
using CiFail.Cli.Output;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail prune` — delete old analyses from history.
///
/// <para>
/// History had no delete path at all: no method on the store, no command, no retention setting.
/// So <c>history.db</c> grew for the life of the install, holding a log excerpt and a term bag per
/// failure — unbounded disk, and an ever-growing pile of log text at rest that SECURITY.md is
/// explicit can contain secrets from the logs you analyzed. "It saved 200 junk rows while I was
/// experimenting" had no answer.
/// </para>
/// </summary>
public sealed class PruneCommand : Command<PruneCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
        [CommandOption("--older-than <DURATION>")]
        [Description("Delete analyses older than this, e.g. 90d, 12w, 6mo. Required.")]
        public string? OlderThan { get; init; }

        [CommandOption("--include-open")]
        [Description("Also delete failures that were never resolved (off by default).")]
        public bool IncludeOpen { get; init; }

        [CommandOption("--dry-run")]
        [Description("Report what would be deleted without deleting it.")]
        public bool DryRun { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.OlderThan))
        {
            CliConsole.Error("prune needs an age: pass [bold]--older-than <duration>[/].");
            CliConsole.Hint("[grey]For example: [bold]cifail prune --older-than 90d --dry-run[/][/]");
            return ExitCodes.Usage;
        }

        if (ParseDuration(settings.OlderThan) is not { } age)
        {
            CliConsole.Error($"could not read the duration '{Markup.Escape(settings.OlderThan)}'.");
            CliConsole.Hint("[grey]Use a number followed by d (days), w (weeks), mo (months) or y (years).[/]");
            return ExitCodes.Usage;
        }

        var cutoff = DateTimeOffset.UtcNow - age;

        return StoreSupport.WithStore(settings, store =>
        {
            if (store is not IPrunableStore prunable)
            {
                CliConsole.Error("this store does not support pruning.");
                CliConsole.Hint("[grey]Pruning is implemented for the local SQLite history; for a " +
                                "shared database, delete rows with your database's own tooling.[/]");
                return ExitCodes.NotUsable;
            }

            var result = prunable.Prune(new PruneRequest(
                Before: cutoff,
                ResolvedOnly: !settings.IncludeOpen,
                DryRun: settings.DryRun));

            Report(result, cutoff, settings);
            return ExitCodes.Ok;
        });
    }

    private static void Report(PruneResult result, DateTimeOffset cutoff, Settings settings)
    {
        var scope = settings.IncludeOpen ? "analyses" : "resolved analyses";
        var when = cutoff.ToLocalTime().ToString("yyyy-MM-dd");

        if (result.Deleted == 0)
        {
            CliConsole.Hint($"[grey]Nothing to prune — no {scope} recorded before {when}.[/]");
            return;
        }

        if (result.DryRun)
        {
            CliConsole.Out.MarkupLine(
                $"[yellow]{Glyphs.Warning}[/] Would delete [bold]{result.Deleted}[/] {scope} " +
                $"recorded before {when}.");
            CliConsole.Hint("[grey]Re-run without [bold]--dry-run[/] to actually delete them.[/]");
            return;
        }

        CliConsole.Out.MarkupLine(
            $"[green]{Glyphs.Check}[/] Deleted [bold]{result.Deleted}[/] {scope} recorded before {when}.");

        if (!settings.IncludeOpen)
            CliConsole.Hint("[grey]Unresolved failures were kept. Pass [bold]--include-open[/] to " +
                            "delete those too.[/]");
    }

    /// <summary>
    /// Parse a human duration: <c>30d</c>, <c>6w</c>, <c>3mo</c>, <c>1y</c>. Returns null when it
    /// doesn't parse — a mistyped age must never be read as "delete everything".
    ///
    /// <para>
    /// Note <c>mo</c> is checked before <c>m</c> would be: a bare <c>m</c> is ambiguous between
    /// minutes and months, so it is simply not accepted.
    /// </para>
    /// </summary>
    internal static TimeSpan? ParseDuration(string value)
    {
        var text = value.Trim().ToLowerInvariant();

        var (suffix, days) = text switch
        {
            _ when text.EndsWith("mo") => ("mo", 30.0),
            _ when text.EndsWith("d") => ("d", 1.0),
            _ when text.EndsWith("w") => ("w", 7.0),
            _ when text.EndsWith("y") => ("y", 365.0),
            _ => (string.Empty, 0.0),
        };

        if (suffix.Length == 0) return null;

        var number = text[..^suffix.Length];
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var count))
            return null;

        // Zero or negative would mean "everything from now backwards", which is not something to
        // infer from a typo.
        return count > 0 ? TimeSpan.FromDays(count * days) : null;
    }
}
