using System.ComponentModel;
using System.Text.Json;
using CiFail.Cli.Output;
using CiFail.Core.Analysis;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail clusters` — group near-duplicate failures from history so you see distinct root causes
/// instead of a flat list. Failures are clustered by similarity of their (scrubbed) log text.
/// </summary>
public sealed class ClustersCommand : Command<ClustersCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
        [CommandOption("--threshold <0..1>")]
        [Description("How similar two failures must be to share a cluster (higher = tighter). Default 0.5.")]
        [DefaultValue(0.5)]
        public double Threshold { get; init; } = 0.5;

        [CommandOption("--since <WHEN>")]
        [Description("Only cluster failures since then: a duration (7d, 24h, 2w) or a date (2026-01-01).")]
        public string? Since { get; init; }

        [CommandOption("--repo <REPO_ID>")]
        [Description("Limit to one repository (the root-commit repo id recorded with each failure).")]
        public string? Repo { get; init; }

        [CommandOption("--top <N>")]
        [Description("How many clusters to show (largest first). Use 0 for all.")]
        [DefaultValue(10)]
        public int Top { get; init; } = 10;

        [CommandOption("--all")]
        [Description("Include singleton clusters (failures that grouped with nothing else).")]
        public bool All { get; init; }

        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a human report.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.Threshold is < 0 or > 1)
        {
            CliConsole.Error("--threshold must be between 0 and 1.");
            return ExitCodes.Usage;
        }

        DateTimeOffset? since;
        try
        {
            since = StatsCommand.ParseSince(settings.Since);
        }
        catch (FormatException ex)
        {
            CliConsole.Error(Markup.Escape(ex.Message));
            return ExitCodes.Usage;
        }

        var query = new ClusterQuery
        {
            Threshold = settings.Threshold,
            Top = Math.Max(0, settings.Top),
            Since = since,
            RepoId = string.IsNullOrWhiteSpace(settings.Repo) ? null : settings.Repo,
            IncludeSingletons = settings.All,
        };

        return StoreSupport.WithStore(settings, store =>
        {
            var result = ClusterService.Compute(store, query);

            if (settings.Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(ClustersJson.ToDto(result), AnalysisJson.Options));
                return ExitCodes.Ok;
            }

            Render(result, query);
            return ExitCodes.Ok;
        });
    }

    private static void Render(ClusterResult r, ClusterQuery query)
    {
        if (r.TotalAnalyzed == 0)
        {
            CliConsole.Hint("[grey]No failures recorded in that window yet. Run [bold]cifail analyze[/] first.[/]");
            return;
        }

        if (r.Clusters.Count == 0)
        {
            CliConsole.Hint($"[grey]Looked at {r.TotalAnalyzed} failures but found no recurring groups " +
                "(every failure is unique). Try [bold]--all[/] to list singletons or a lower [bold]--threshold[/].[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]failure clusters[/] [grey](of {r.TotalAnalyzed} failures, threshold {query.Threshold:0.##})[/]");
        table.AddColumn(new TableColumn("size").RightAligned());
        table.AddColumn("root cause");
        table.AddColumn("ecosystems");
        table.AddColumn("last seen");
        table.AddColumn("ids");

        foreach (var c in r.Clusters)
        {
            var ids = string.Join(", ", c.MemberIds.Take(6).Select(id => $"#{id}"));
            if (c.MemberIds.Count > 6) ids += $" [grey]+{c.MemberIds.Count - 6}[/]";
            table.AddRow(
                c.Count.ToString(),
                Markup.Escape(c.Label),
                Markup.Escape(string.Join(", ", c.Ecosystems)),
                Markup.Escape(c.LastSeen.LocalDateTime.ToString("yyyy-MM-dd")),
                ids);
        }

        CliConsole.Out.Write(table);
    }
}
