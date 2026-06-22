using System.ComponentModel;
using System.Text.RegularExpressions;
using CiFail.Core.Ingest;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail rules test &lt;regex&gt; [--file log] [--stdin]` — compile a regex and run it
/// against a log, printing the matched line(s) and any named captures. Fast feedback while
/// authoring a rule, before wiring it into a pack.
/// </summary>
public sealed class RulesTestCommand : Command<RulesTestCommand.Settings>
{
    private const int MaxMatchesShown = 10;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<regex>")]
        [Description("The rule 'match' regex to try (use quotes).")]
        public string Regex { get; init; } = string.Empty;

        [CommandOption("-f|--file <LOG>")]
        [Description("Log file to test against. Omit to read stdin.")]
        public string? File { get; init; }

        [CommandOption("--stdin")]
        [Description("Read the log from stdin (the default when no --file is given).")]
        public bool Stdin { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        Regex regex;
        try
        {
            // Mirror the engine's options so behaviour matches a real rule match.
            regex = new Regex(settings.Regex, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] invalid regex — {Markup.Escape(ex.Message)}");
            return 2;
        }

        string raw;
        try
        {
            raw = ReadLog(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            AnsiConsole.MarkupLine("[yellow]no log to test against.[/] Pass [bold]--file <log>[/] or pipe one in.");
            return 2;
        }

        // Normalize exactly like analyze (strip ANSI / CI timestamps) so what you test is
        // what the engine would see.
        var text = LogNormalizer.Build("test", raw).NormalizedText;
        var matches = regex.Matches(text);

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no match.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]{matches.Count} match(es).[/]");
        var groupNames = regex.GetGroupNames().Where(n => !int.TryParse(n, out _)).ToArray();

        foreach (var m in matches.Take(MaxMatchesShown).Cast<Match>())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [bold]line:[/] {Markup.Escape(LineAt(text, m.Index))}");
            foreach (var name in groupNames)
            {
                var g = m.Groups[name];
                if (g.Success)
                    AnsiConsole.MarkupLine($"    [grey]{{{Markup.Escape(name)}}}[/] = {Markup.Escape(g.Value.Trim())}");
            }
        }

        if (matches.Count > MaxMatchesShown)
            AnsiConsole.MarkupLine($"  [grey]… and {matches.Count - MaxMatchesShown} more[/]");

        return 0;
    }

    private static string ReadLog(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            if (!File.Exists(settings.File))
                throw new FileNotFoundException($"file not found: {settings.File}");
            return File.ReadAllText(settings.File);
        }

        return Console.IsInputRedirected ? Console.In.ReadToEnd() : string.Empty;
    }

    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }
}
