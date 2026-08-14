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
        [Description("How many analyses to list.")]
        [DefaultValue(20)]
        public int Limit { get; init; } = 20;

        [CommandOption("--offset <N>")]
        [Description("Skip this many rows, for paging through a long history.")]
        public int Offset { get; init; }

        [CommandOption("--since <DATE>")]
        [Description("Only analyses at or after this date (e.g. 2026-01-01).")]
        public string? Since { get; init; }

        [CommandOption("--repo <ID>")]
        [Description("Only analyses from this repository id.")]
        public string? Repo { get; init; }

        [CommandOption("--ecosystem <NAME>")]
        [Description("Only analyses detected as this ecosystem.")]
        public string? Ecosystem { get; init; }

        [CommandOption("--rule <ID>")]
        [Description("Only analyses whose root cause was this rule.")]
        public string? Rule { get; init; }

        [CommandOption("--open")]
        [Description("Only failures that are still unresolved.")]
        public bool Open { get; init; }

        [CommandOption("--resolved")]
        [Description("Only failures that have been resolved.")]
        public bool Resolved { get; init; }

        [CommandOption("--search <TEXT>")]
        [Description("Only analyses whose source, fingerprint or excerpt contains this text.")]
        public string? Search { get; init; }

        [CommandOption("--json")]
        [Description("Print the result as JSON instead of a table.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.Open && settings.Resolved)
        {
            CliConsole.Error("--open and --resolved are mutually exclusive.");
            return ExitCodes.Usage;
        }

        if (settings.Since is not null && !DateTimeOffset.TryParse(settings.Since, out _))
        {
            CliConsole.Error($"could not read the date '{Markup.Escape(settings.Since)}'.");
            CliConsole.Hint("[grey]Use a date like [bold]2026-01-31[/] or a full timestamp.[/]");
            return ExitCodes.Usage;
        }

        return StoreSupport.WithStore(settings, store => settings.Id is { } id
            ? ShowOne(store, id, settings.Json)
            : ShowList(store, settings));
    }

    private static HistoryQuery BuildQuery(Settings s) => new()
    {
        Limit = Math.Max(1, s.Limit),
        Offset = Math.Max(0, s.Offset),
        Since = DateTimeOffset.TryParse(s.Since, out var since) ? since : null,
        RepoId = string.IsNullOrWhiteSpace(s.Repo) ? null : s.Repo,
        Ecosystem = string.IsNullOrWhiteSpace(s.Ecosystem) ? null : s.Ecosystem,
        RuleId = string.IsNullOrWhiteSpace(s.Rule) ? null : s.Rule,
        Status = s.Open ? AnalysisStatus.Open : s.Resolved ? AnalysisStatus.Resolved : null,
        Search = string.IsNullOrWhiteSpace(s.Search) ? null : s.Search,
    };

    private static int ShowList(IAnalysisStore store, Settings settings)
    {
        var query = BuildQuery(settings);
        var page = HistoryService.Query(store, query);
        var rows = page.Items;

        // history was the one data-bearing command with no machine-readable output, which made
        // the whole history unusable from a script even though every record is fully structured.
        if (settings.Json)
        {
            var dtos = rows.Select(StoredAnalysisJson.ToDto).ToList();
            Console.Out.WriteLine(JsonSerializer.Serialize(dtos, AnalysisJson.Options));
            return ExitCodes.Ok;
        }

        if (rows.Count == 0)
        {
            // Not the answer, just an explanation for the empty output — so stderr, keeping
            // stdout clean for whatever the caller is piping this into.
            CliConsole.Hint(query.IsUnfiltered
                ? "[grey]No analyses recorded yet. Run [bold]cifail analyze[/] first.[/]"
                : "[grey]No analyses match those filters.[/]");
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

        // Paging only matters once there is more than a page; and a total that came from the
        // in-memory fallback is a floor, so it is shown as "230+" rather than claimed as exact.
        if (page.Total > rows.Count)
        {
            var total = page.Truncated ? $"{page.Total}+" : page.Total.ToString();
            var shown = settings.Offset + rows.Count;
            CliConsole.Hint($"[grey]Showing {settings.Offset + 1}{Glyphs.Dash}{shown} of {total}. " +
                            $"Next page: [bold]--offset {shown}[/][/]");
        }

        return ExitCodes.Ok;
    }

    private static int ShowOne(IAnalysisStore store, long id, bool json)
    {
        var r = store.GetById(id);
        if (r is null)
        {
            if (json) Console.Out.WriteLine("null");
            CliConsole.Error($"no analysis with id {id}.");
            CliConsole.Hint("[grey]Run [bold]cifail history[/] to see the ids you have.[/]");
            return ExitCodes.NotFound;
        }

        if (json)
        {
            Console.Out.WriteLine(
                JsonSerializer.Serialize(StoredAnalysisJson.ToDto(r), AnalysisJson.Options));
            return ExitCodes.Ok;
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
