using CiFail.Cli.Output;
using CiFail.Core.Analysis;
using CiFail.Core.Git;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail reconcile` — scan this repo's open failures and auto-resolve any recorded at an
/// older commit (HEAD has moved past them). Normally happens automatically during
/// `cifail analyze`; this runs it on demand, e.g. from a git hook installed by `cifail init`.
/// </summary>
public sealed class ReconcileCommand : Command<ReconcileCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // The repo lives on the client, so reconciliation always runs here — against either a
        // local database or a remote --server (R11). The git context is detected locally; the
        // store (local or http) only supplies open failures and records the auto-resolutions.
        var git = GitContext.Detect(Directory.GetCurrentDirectory());
        if (git is null)
        {
            CliConsole.Error("cifail reconcile only works inside a git repository.");
            return ExitCodes.DependencyUnavailable;
        }

        return StoreSupport.WithStore(settings, store =>
        {
            // No observed fingerprints: this is a pure "anything from an older commit is likely
            // fixed" pass, so it credits the moved-past commits.
            var resolved = ResolutionReconciler.Reconcile(store, git);

            if (resolved.Count == 0)
            {
                CliConsole.Hint("[grey]Nothing to reconcile — no open failures from older commits.[/]");
                return ExitCodes.Ok;
            }

            var noun = resolved.Count == 1 ? "failure" : "failures";
            CliConsole.Out.MarkupLine($"[green]{Glyphs.Check}[/] Auto-resolved {resolved.Count} past {noun}:");
            foreach (var r in resolved)
            {
                var shortSha = r.Commit.Length >= 7 ? r.Commit[..7] : r.Commit;
                CliConsole.Out.MarkupLine($"  [grey]#{r.Id}[/] likely fixed by [bold]{Markup.Escape(shortSha)}[/]");
            }
            return ExitCodes.Ok;
        });
    }
}
