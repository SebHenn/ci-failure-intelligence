using CiFail.Core.Models;
using Spectre.Console;

namespace CiFail.Cli.Output;

/// <summary>Renders an <see cref="Analysis"/> for humans using Spectre panels/tables.</summary>
public static class ConsoleRenderer
{
    public static void Render(IAnsiConsole console, Analysis analysis)
    {
        console.Write(new Rule($"[bold]cifail[/] · {Markup.Escape(analysis.Source)}").LeftJustified());

        if (analysis.RootCause is { } rc)
            RenderRootCause(console, analysis, rc);
        else
            RenderNoMatch(console, analysis);

        if (analysis.SimilarFailures.Count > 0)
            RenderSimilar(console, analysis.SimilarFailures);

        if (analysis.AiSuggestion is { } ai)
            RenderAi(console, ai);
    }

    private static void RenderRootCause(IAnsiConsole console, Analysis analysis, RuleMatch rc)
    {
        var color = ConfidenceColor(rc.Score);
        var header = $"[{color}]{Markup.Escape(rc.Rule.Title)}[/]  " +
                     $"[grey]({analysis.Ecosystem.ToString().ToLowerInvariant()} · " +
                     $"{Markup.Escape(rc.Rule.Category)} · {rc.Score:0.00})[/]";

        var body = new Markup(
            $"{Markup.Escape(rc.Fix.TrimEnd())}\n\n" +
            $"[grey]matched:[/] {Markup.Escape(Truncate(rc.MatchedLine, 100))}" +
            (string.IsNullOrWhiteSpace(rc.Rule.Docs)
                ? string.Empty
                : $"\n[grey]docs:[/] [link]{Markup.Escape(rc.Rule.Docs!)}[/]"));

        console.Write(new Panel(body)
            .Header($" Root cause: {header} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(ConfidenceSpectreColor(rc.Score))
            .Expand());

        if (analysis.Matches.Count > 1)
        {
            var table = new Table().Border(TableBorder.Minimal).Title("[grey]other signals[/]");
            table.AddColumn("rule");
            table.AddColumn("title");
            table.AddColumn(new TableColumn("conf").RightAligned());
            foreach (var m in analysis.Matches.Skip(1).Take(5))
                table.AddRow(Markup.Escape(m.Rule.Id), Markup.Escape(m.Rule.Title), $"{m.Score:0.00}");
            console.Write(table);
        }

        console.MarkupLine($"[grey]fingerprint:[/] {Markup.Escape(analysis.Fingerprint.ToString())}");
    }

    private static void RenderNoMatch(IAnsiConsole console, Analysis analysis)
    {
        console.Write(new Panel(new Markup(
                "[yellow]No known failure pattern matched.[/]\n\n" +
                "This failure isn't in the rule packs yet. Try [bold]--ai[/] for a suggestion " +
                "(requires a local Ollama), or add a rule pack to teach cifail this pattern."))
            .Header(" Root cause: [yellow]unknown[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Expand());

        console.MarkupLine(
            $"[grey]ecosystem:[/] {analysis.Ecosystem.ToString().ToLowerInvariant()}   " +
            $"[grey]fingerprint:[/] {Markup.Escape(analysis.Fingerprint.ToString())}");
    }

    private static void RenderSimilar(IAnsiConsole console, IReadOnlyList<SimilarFailure> similar)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Similar past failures[/]");
        table.AddColumn("id");
        table.AddColumn(new TableColumn("match").RightAligned());
        table.AddColumn("rule");
        table.AddColumn("when");
        table.AddColumn("resolution");

        foreach (var s in similar)
        {
            table.AddRow(
                s.Id.ToString(),
                $"{s.Similarity:0.00}",
                Markup.Escape(s.RuleId),
                Markup.Escape(s.AnalyzedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")),
                Markup.Escape(string.IsNullOrWhiteSpace(s.Resolution) ? "—" : Truncate(s.Resolution!, 50)));
        }

        console.Write(table);
    }

    private static void RenderAi(IAnsiConsole console, AiSuggestion ai)
    {
        console.Write(new Panel(new Markup(
                $"{Markup.Escape(ai.RootCause)}\n\n[bold]Suggested fix[/]\n{Markup.Escape(ai.Fix)}"))
            .Header($" AI suggestion [grey]({Markup.Escape(ai.Model)})[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Expand());
    }

    private static string ConfidenceColor(double score) => score switch
    {
        >= 0.8 => "green",
        >= 0.6 => "yellow",
        _ => "grey",
    };

    private static Color ConfidenceSpectreColor(double score) => score switch
    {
        >= 0.8 => Color.Green,
        >= 0.6 => Color.Yellow,
        _ => Color.Grey,
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
