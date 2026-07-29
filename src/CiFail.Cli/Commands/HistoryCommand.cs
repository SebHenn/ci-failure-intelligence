using System.ComponentModel;
using CiFail.Cli.Output;
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
        => StoreSupport.WithStore(settings, store => settings.Id is { } id
            ? ShowOne(store, id)
            : ShowList(store, settings.Limit));

    private static int ShowList(IAnalysisStore store, int limit)
    {
        var rows = store.GetRecent(limit);
        if (rows.Count == 0)
        {
            // Not the answer, just an explanation for the empty output — so stderr, keeping
            // stdout clean for whatever the caller is piping this into.
            CliConsole.Hint("[grey]No analyses recorded yet. Run [bold]cifail analyze[/] first.[/]");
            return ExitCodes.Ok;
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

        CliConsole.Out.Write(table);
        return ExitCodes.Ok;
    }

    private static int ShowOne(IAnalysisStore store, long id)
    {
        var r = store.GetById(id);
        if (r is null)
        {
            CliConsole.Error($"no analysis with id {id}.");
            CliConsole.Hint("[grey]Run [bold]cifail history[/] to see the ids you have.[/]");
            return ExitCodes.NotFound;
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

        CliConsole.Out.Write(new Panel(grid).Header($" analysis #{r.Id} ").Border(BoxBorder.Rounded));
        CliConsole.Out.Write(new Panel(new Markup(Markup.Escape(r.Excerpt)))
            .Header(" excerpt ").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
        return ExitCodes.Ok;
    }

    private static string ResolvedCell(StoredAnalysis r)
    {
        if (r.Resolution is null) return $"[grey]{Glyphs.Dash}[/]";
        return r.ResolutionSource == ResolutionSource.Auto
            ? $"[blue]{Glyphs.Check} auto[/]"
            : $"[green]{Glyphs.Check}[/]";
    }

    private static string ResolutionDetail(StoredAnalysis r)
    {
        if (r.Resolution is null) return $"[grey]{Glyphs.Dash}[/]";

        var when = r.ResolvedAt?.LocalDateTime.ToString("yyyy-MM-dd");
        var tag = r.ResolutionSource == ResolutionSource.Auto
            ? $"[blue](auto{(r.ResolvedCommit is null ? "" : $", {Short(r.ResolvedCommit)}")})[/]"
            : "[green](manual)[/]";
        return $"{Markup.Escape(r.Resolution)} {tag} [grey]{when}[/]";
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    /// <summary>Keep the tail (where the interesting part of a path is), marked with a leading ellipsis.</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        // The ellipsis is 1 char in Unicode but 3 in the ASCII fallback, so the room it needs
        // has to be subtracted rather than assumed.
        var keep = Math.Max(0, max - Glyphs.Ellipsis.Length);
        return Glyphs.Ellipsis + s[^keep..];
    }
}
