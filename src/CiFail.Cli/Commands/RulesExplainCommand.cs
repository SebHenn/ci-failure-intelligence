using System.ComponentModel;
using CiFail.Cli.Output;
using CiFail.Core.Rules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail rules explain &lt;id&gt;` — print one rule's full definition and whether it's an
/// embedded default or a user override.
/// </summary>
public sealed class RulesExplainCommand : Command<RulesExplainCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("The rule id to explain (e.g. nuget-nu1101).")]
        public string Id { get; init; } = string.Empty;
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var rule = RulePackLoader.LoadAll()
            .FirstOrDefault(r => string.Equals(r.Id, settings.Id, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            CliConsole.Error($"no rule with id '{Markup.Escape(settings.Id)}'.");
            CliConsole.Hint("[grey]Run [bold]cifail rules list[/] to see them all.[/]");
            return ExitCodes.NotFound;
        }

        var source = DescribeSource(rule.Id);

        var grid = new Grid().AddColumn().AddColumn();
        void Row(string k, string v) => grid.AddRow($"[grey]{k}[/]", Markup.Escape(v));

        Row("id", rule.Id);
        Row("ecosystem", rule.Ecosystem);
        Row("category", rule.Category);
        Row("confidence", $"{rule.Confidence:0.00}");
        Row("source", source);
        if (!string.IsNullOrWhiteSpace(rule.Docs)) Row("docs", rule.Docs!);

        CliConsole.Out.Write(new Panel(grid)
            .Header($"[bold]{Markup.Escape(rule.Title)}[/]")
            .Border(BoxBorder.Rounded));

        CliConsole.Out.MarkupLine("[bold]match[/] (regex):");
        CliConsole.Out.MarkupLine($"  [grey]{Markup.Escape(rule.Match)}[/]");
        CliConsole.Out.WriteLine();
        CliConsole.Out.MarkupLine("[bold]fix[/] (template):");
        foreach (var line in rule.Fix.TrimEnd().Split('\n'))
            CliConsole.Out.MarkupLine($"  {Markup.Escape(line.TrimEnd())}");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Where the rule that actually won came from — naming the directory, not just "user pack".
    ///
    /// <para>
    /// This used to check <c>~/.cifail/rules</c> alone while loading via the whole search path, so
    /// a rule committed to a repository's <c>.cifail/rules/</c> — the case R14 exists for — was
    /// reported as <c>embedded default</c>. "Which file does this rule live in?" is the entire
    /// question <c>rules explain</c> is asked, and it answered it wrongly for the newest and most
    /// interesting tier.
    /// </para>
    ///
    /// <para>
    /// <see cref="RuleSearchPath.Resolve"/> returns directories most general first and later wins
    /// on a duplicate id, so the winner is the <i>last</i> directory defining it.
    /// </para>
    /// </summary>
    private static string DescribeSource(string id)
    {
        bool Defines(string dir) =>
            Directory.Exists(dir) &&
            Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories)
                .Any(file => RulePackLoader.ParseDocument(SafeRead(file))
                    .Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)));

        var winner = RuleSearchPath.Resolve().LastOrDefault(Defines);
        var embedded = RulePackLoader.LoadEmbedded()
            .Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        if (winner is null)
            return "embedded default";

        return embedded
            ? $"{winner} (overrides an embedded default)"
            : winner;
    }

    /// <summary>
    /// A pack that cifail merely <i>found</i> must never break a command; an unreadable or
    /// unparseable one is named by <c>rules validate</c> instead.
    /// </summary>
    private static string SafeRead(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
