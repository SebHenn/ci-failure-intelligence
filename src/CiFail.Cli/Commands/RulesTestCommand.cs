using System.ComponentModel;
using System.Text.RegularExpressions;
using CiFail.Cli.Output;
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

    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "<regex>")]
        [Description("The rule 'match' regex to try (use quotes).")]
        public string Regex { get; init; } = string.Empty;

        [CommandOption("-f|--file <LOG>")]
        [Description("Log file to test against. Omit to read stdin.")]
        public string? File { get; init; }

        [CommandOption("--stdin")]
        [Description("Read the log from stdin, ignoring --file.")]
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
            CliConsole.Error($"invalid regex — {Markup.Escape(ex.Message)}");
            return ExitCodes.Usage;
        }

        string raw;
        try
        {
            raw = ReadLog(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliConsole.Error(Markup.Escape(ex.Message));
            return ExitCodes.Usage;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            CliConsole.Error("no log to test against.");
            CliConsole.Hint("[grey]Pass [bold]--file <log>[/] or pipe one in.[/]");
            return ExitCodes.Usage;
        }

        // Normalize exactly like analyze (strip ANSI / CI timestamps) so what you test is
        // what the engine would see.
        var text = LogNormalizer.Build("test", raw).NormalizedText;
        var matches = regex.Matches(text);

        if (matches.Count == 0)
        {
            // Stays on stdout: "did my regex match?" is the question this command answers, so
            // "no" is the answer, not a diagnostic.
            CliConsole.Out.MarkupLine("[yellow]no match.[/]");
            return ExitCodes.Negative;
        }

        CliConsole.Out.MarkupLine($"[green]{matches.Count} match(es).[/]");
        var groupNames = regex.GetGroupNames().Where(n => !int.TryParse(n, out _)).ToArray();

        foreach (var m in matches.Take(MaxMatchesShown).Cast<Match>())
        {
            CliConsole.Out.WriteLine();
            CliConsole.Out.MarkupLine($"  [bold]line:[/] {Markup.Escape(LineAt(text, m.Index))}");
            foreach (var name in groupNames)
            {
                var g = m.Groups[name];
                if (g.Success)
                    CliConsole.Out.MarkupLine($"    [grey]{{{Markup.Escape(name)}}}[/] = {Markup.Escape(g.Value.Trim())}");
            }
        }

        if (matches.Count > MaxMatchesShown)
            CliConsole.Out.MarkupLine($"  [grey]{Glyphs.Ellipsis} and {matches.Count - MaxMatchesShown} more[/]");

        return ExitCodes.Ok;
    }

    private static string ReadLog(Settings settings)
    {
        // --stdin was accepted but never read, so `--stdin --file x` silently used the file.
        // Now the explicit flag wins over the implicit --file default.
        if (!settings.Stdin && !string.IsNullOrWhiteSpace(settings.File))
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
