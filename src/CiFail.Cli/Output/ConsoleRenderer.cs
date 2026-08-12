using CiFail.Core.Models;
using Spectre.Console;

namespace CiFail.Cli.Output;

/// <summary>
/// Renders an <see cref="Analysis"/> for humans using Spectre panels/tables. Wording
/// is intentionally plain so someone new to CI logs can act on it without extra context.
/// </summary>
public static class ConsoleRenderer
{
    /// <summary>
    /// What the header spends on things that aren't the source: "cifail" + the separator dot
    /// and its spaces, plus the rule's own border and padding.
    /// </summary>
    private const int HeaderChrome = 14;

    /// <summary>Never shrink the source below this, however narrow the console claims to be.</summary>
    private const int MinSourceWidth = 12;

    public static void Render(IAnsiConsole console, Analysis analysis)
    {
        // Shorten before handing it to Spectre: its own truncation drops the whole path
        // (see PathDisplay), and the path is what says which log this report is about.
        var source = PathDisplay.Elide(analysis.Source, Math.Max(MinSourceWidth, console.Profile.Width - HeaderChrome));
        console.Write(new Rule($"[bold]cifail[/] {Glyphs.Dot} {Markup.Escape(source)}").LeftJustified());

        if (analysis.RootCause is { } rc)
            RenderRootCause(console, analysis, rc);
        else
            RenderNoMatch(console, analysis);

        if (analysis.SimilarFailures.Count > 0)
            RenderSimilar(console, analysis.SimilarFailures);

        if (analysis.AiSuggestion is { } ai)
            RenderAi(console, ai);

        RenderNextSteps(console, analysis);
    }

    private static void RenderRootCause(IAnsiConsole console, Analysis analysis, RuleMatch rc)
    {
        var color = ConfidenceColor(rc.Score);
        var header = $"[{color}]{Markup.Escape(rc.Rule.Title)}[/]  " +
                     $"[grey]({analysis.Ecosystem.ToString().ToLowerInvariant()} {Glyphs.Dot} " +
                     $"{Markup.Escape(rc.Rule.Category)} {Glyphs.Dot} {ConfidenceWord(rc.Score)} confidence)[/]";

        var body = new Markup(
            "[bold]How to fix it[/]\n" +
            $"{Markup.Escape(rc.Fix.TrimEnd())}\n\n" +
            $"[grey]The line that gave it away:[/]\n{Markup.Escape(Truncate(rc.MatchedLine, 100))}" +
            (string.IsNullOrWhiteSpace(rc.Rule.Docs)
                ? string.Empty
                : $"\n\n[grey]Learn more:[/] [link]{Markup.Escape(rc.Rule.Docs!)}[/]"));

        console.Write(new Panel(body)
            .Header($" What broke: {header} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(ConfidenceSpectreColor(rc.Score))
            .Expand());

        if (analysis.Matches.Count > 1)
        {
            var table = new Table().Border(TableBorder.Minimal)
                .Title("[grey]Other things cifail noticed (less likely the main cause)[/]");
            table.AddColumn("problem");
            table.AddColumn(new TableColumn("confidence").RightAligned());
            foreach (var m in analysis.Matches.Skip(1).Take(5))
                table.AddRow(Markup.Escape(m.Rule.Title), ConfidenceWord(m.Score));
            console.Write(table);
        }
    }

    private static void RenderNoMatch(IAnsiConsole console, Analysis analysis)
    {
        console.Write(new Panel(new Markup(
                "[yellow]cifail doesn't recognize this failure yet.[/]\n\n" +
                "It looks like a [bold]" + analysis.Ecosystem.ToString().ToLowerInvariant() +
                "[/] log, but none of the built-in patterns matched. You can:\n" +
                $"  {Glyphs.Bullet} read the log yourself and look for the first line containing [bold]error[/] or [bold]failed[/]\n" +
                $"  {Glyphs.Bullet} try [bold]--ai[/] to ask a local AI model (needs Ollama installed)\n" +
                $"  {Glyphs.Bullet} teach cifail this pattern by adding a rule (see the README)"))
            .Header(" What broke: [yellow]not sure yet[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Expand());
    }

    private static void RenderSimilar(IAnsiConsole console, IReadOnlyList<SimilarFailure> similar)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .Title("[bold]You've seen something like this before[/]");
        table.AddColumn("id");
        table.AddColumn(new TableColumn("how similar").RightAligned());
        table.AddColumn("problem");
        table.AddColumn("when");
        table.AddColumn("how you fixed it last time");

        foreach (var s in similar)
        {
            table.AddRow(
                s.Id.ToString(),
                $"{s.Similarity:0%}",
                Markup.Escape(s.RuleId),
                Markup.Escape(s.AnalyzedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")),
                Markup.Escape(string.IsNullOrWhiteSpace(s.Resolution) ? $"{Glyphs.Dash} (not recorded)" : Truncate(s.Resolution!, 50)));
        }

        console.Write(table);
        console.MarkupLine("[grey]See the full detail of a past failure with[/] [bold]cifail history <id>[/][grey].[/]");
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

    private static void RenderNextSteps(IAnsiConsole console, Analysis analysis)
    {
        // Once they've fixed it, let them record how — that's what powers the
        // "you've seen this before" hint next time. Only show when we have an id.
        if (analysis.HistoryId is { } id)
        {
            console.MarkupLine(
                $"[grey]Saved as #{id}. Once you've fixed it, record how so future-you remembers:[/]\n" +
                $"  [bold]cifail resolve {id} --note \"what fixed it\"[/]");
        }
    }

    private static string ConfidenceWord(double score) => score switch
    {
        >= 0.8 => "high",
        >= 0.6 => "medium",
        _ => "low",
    };

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

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        // The ellipsis is 1 char in Unicode but 3 in the ASCII fallback, so the room it needs
        // has to be subtracted rather than assumed.
        var keep = Math.Max(0, max - Glyphs.Ellipsis.Length);
        return s[..keep] + Glyphs.Ellipsis;
    }
}
