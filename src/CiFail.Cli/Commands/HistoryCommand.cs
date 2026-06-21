using System.ComponentModel;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail history [id]` — browse past analyses, or show one in detail when an id
/// is given.
/// </summary>
public sealed class HistoryCommand : Command<HistoryCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Show full detail for a single analysis id.")]
        public long? Id { get; init; }

        [CommandOption("-n|--limit <N>")]
        [Description("How many recent analyses to list.")]
        [DefaultValue(20)]
        public int Limit { get; init; } = 20;
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var store = StoreSupport.TryCreate(settings);
        if (store is null) return 2;

        return settings.Id is { } id
            ? ShowOne(store, id)
            : ShowList(store, settings.Limit);
    }

    private static int ShowList(IAnalysisStore store, int limit)
    {
        var rows = store.GetRecent(limit);
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No analyses recorded yet. Run [bold]cifail analyze[/] first.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]cifail history[/]");
        table.AddColumn(new TableColumn("id").RightAligned());
        table.AddColumn("when");
        table.AddColumn("ecosystem");
        table.AddColumn("rule");
        table.AddColumn("source");
        table.AddColumn("resolved");

        foreach (var r in rows)
        {
            table.AddRow(
                r.Id.ToString(),
                Markup.Escape(r.AnalyzedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")),
                Markup.Escape(r.Ecosystem),
                Markup.Escape(r.Matched ? r.RuleId : "[unknown]"),
                Markup.Escape(Truncate(r.Source, 40)),
                ResolvedCell(r));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ShowOne(IAnalysisStore store, long id)
    {
        var r = store.GetById(id);
        if (r is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no analysis with id {id}.");
            return 2;
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]id[/]", r.Id.ToString());
        grid.AddRow("[grey]analyzed[/]", Markup.Escape(r.AnalyzedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
        grid.AddRow("[grey]ecosystem[/]", Markup.Escape(r.Ecosystem));
        grid.AddRow("[grey]rule[/]", Markup.Escape(r.Matched ? r.RuleId : "[unknown]"));
        grid.AddRow("[grey]fingerprint[/]", Markup.Escape(r.Fingerprint));
        grid.AddRow("[grey]source[/]", Markup.Escape(r.Source));
        if (r.GitCommit is not null)
            grid.AddRow("[grey]commit[/]", $"{Markup.Escape(Short(r.GitCommit))}"
                + (r.GitBranch is null ? "" : $" [grey]({Markup.Escape(r.GitBranch)})[/]")
                + (r.GitDirty ? " [yellow](dirty)[/]" : ""));
        grid.AddRow("[grey]resolution[/]", ResolutionDetail(r));

        AnsiConsole.Write(new Panel(grid).Header($" analysis #{r.Id} ").Border(BoxBorder.Rounded));
        AnsiConsole.Write(new Panel(new Markup(Markup.Escape(r.Excerpt)))
            .Header(" excerpt ").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
        return 0;
    }

    private static string ResolvedCell(StoredAnalysis r)
    {
        if (r.Resolution is null) return "[grey]—[/]";
        return r.ResolutionSource == ResolutionSource.Auto ? "[blue]✓ auto[/]" : "[green]✓[/]";
    }

    private static string ResolutionDetail(StoredAnalysis r)
    {
        if (r.Resolution is null) return "[grey]—[/]";

        var when = r.ResolvedAt?.LocalDateTime.ToString("yyyy-MM-dd");
        var tag = r.ResolutionSource == ResolutionSource.Auto
            ? $"[blue](auto{(r.ResolvedCommit is null ? "" : $", {Short(r.ResolvedCommit)}")})[/]"
            : "[green](manual)[/]";
        return $"{Markup.Escape(r.Resolution)} {tag} [grey]{when}[/]";
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];
}
