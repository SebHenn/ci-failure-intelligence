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
            RenderEvidence(rc, console.Profile.Width) +
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
            // --verbose lifts the cap: the secondary matches are exactly what you want when the
            // top one is wrong, and "and 7 more you can't see" is not a useful state to be in.
            var secondary = analysis.Matches.Skip(1);
            var shown = CliConsole.Verbose ? secondary.ToList() : secondary.Take(5).ToList();
            var hidden = analysis.Matches.Count - 1 - shown.Count;

            var table = new Table().Border(TableBorder.Minimal)
                .Title("[grey]Other things cifail noticed (less likely the main cause)[/]");
            table.AddColumn("problem");
            table.AddColumn(new TableColumn("confidence").RightAligned());
            if (CliConsole.Verbose) table.AddColumn("rule");

            foreach (var m in shown)
            {
                if (CliConsole.Verbose)
                    table.AddRow(Markup.Escape(m.Rule.Title), ConfidenceWord(m.Score), Markup.Escape(m.Rule.Id));
                else
                    table.AddRow(Markup.Escape(m.Rule.Title), ConfidenceWord(m.Score));
            }

            console.Write(table);

            if (hidden > 0)
                CliConsole.Hint($"[grey]{hidden} more match{(hidden == 1 ? "" : "es")} not shown " +
                                $"{Glyphs.Dash} run again with [bold]--verbose[/] to see them.[/]");
        }
    }

    /// <summary>
    /// The evidence block: the matched line with its surrounding log, each line numbered and the
    /// matched one marked.
    ///
    /// <para>
    /// Every release before this showed a single line truncated to 100 characters, which for a
    /// compiler error threw away the "expected X, got Y" underneath it — the part that says what
    /// to change. Numbering the lines makes the report locatable in the original log.
    /// </para>
    /// </summary>
    private static string RenderEvidence(RuleMatch rc, int consoleWidth)
    {
        var occurrences = rc.OccurrenceCount > 1
            ? $" [grey]({Occurrences(rc)})[/]"
            : string.Empty;

        var heading = rc.LineNumber > 0
            ? $"[grey]The line that gave it away[/] [grey](line {rc.LineNumber})[/]{occurrences}[grey]:[/]"
            : $"[grey]The line that gave it away{occurrences}:[/]";

        // Leave room for the gutter ("  1234 > ") and the panel's own border/padding.
        var width = Math.Max(MinSourceWidth, consoleWidth - EvidenceChrome);

        var block = rc.ContextBlock.ToList();
        if (block.Count == 1 && rc.LineNumber == 0)
            return $"{heading}\n{Markup.Escape(Truncate(rc.MatchedLine, width))}";

        var gutter = Math.Max(3, (rc.ContextStartLine + block.Count).ToString().Length);
        var lines = new List<string>(block.Count);

        for (var i = 0; i < block.Count; i++)
        {
            var number = rc.ContextStartLine + i;
            var isMatch = number == rc.LineNumber;
            var text = Markup.Escape(Truncate(block[i], width));

            // The marker has to survive a console that can't render the glyph, hence Glyphs.
            lines.Add(isMatch
                ? $"[grey]{number.ToString().PadLeft(gutter)}[/] [red]{Glyphs.Bullet}[/] [bold]{text}[/]"
                : $"[grey]{number.ToString().PadLeft(gutter)}   {text}[/]");
        }

        return $"{heading}\n{string.Join("\n", lines)}";
    }

    private static string Occurrences(RuleMatch rc) =>
        rc.OccurrenceCount >= Core.Rules.RuleEngine.MaxCountedOccurrences
            ? $"{rc.OccurrenceCount}+ times"
            : $"{rc.OccurrenceCount} times";

    /// <summary>Gutter ("1234 > ") plus the panel border and padding.</summary>
    private const int EvidenceChrome = 16;

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
        // The identifiers every follow-up command needs. Neither used to appear anywhere in the
        // human view: you could read the whole report and still not know what to pass to
        // `cifail rules explain`, or what line a gate baseline would key on — both were
        // reachable only through --json.
        console.WriteLine();
        if (analysis.RootCause is { } rc)
            console.MarkupLine($"[grey]Rule[/] [bold]{Markup.Escape(rc.Rule.Id)}[/] " +
                               $"[grey]{Glyphs.Dot} explain it with[/] [bold]cifail rules explain {Markup.Escape(rc.Rule.Id)}[/]");

        console.MarkupLine($"[grey]Fingerprint[/] [bold]{Markup.Escape(analysis.Fingerprint.ToString())}[/]");

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
