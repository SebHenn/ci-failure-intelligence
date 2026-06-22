using System.ComponentModel;
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
            AnsiConsole.MarkupLine($"[red]error:[/] no rule with id '{Markup.Escape(settings.Id)}'. " +
                "Run [bold]cifail rules list[/] to see them all.");
            return 1;
        }

        var embeddedIds = RulePackLoader.LoadEmbedded()
            .Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userIds = UserRuleIds();

        var source = userIds.Contains(rule.Id)
            ? (embeddedIds.Contains(rule.Id) ? "user pack (overrides an embedded default)" : "user pack")
            : "embedded default";

        var grid = new Grid().AddColumn().AddColumn();
        void Row(string k, string v) => grid.AddRow($"[grey]{k}[/]", Markup.Escape(v));

        Row("id", rule.Id);
        Row("ecosystem", rule.Ecosystem);
        Row("category", rule.Category);
        Row("confidence", $"{rule.Confidence:0.00}");
        Row("source", source);
        if (!string.IsNullOrWhiteSpace(rule.Docs)) Row("docs", rule.Docs!);

        AnsiConsole.Write(new Panel(grid)
            .Header($"[bold]{Markup.Escape(rule.Title)}[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.MarkupLine("[bold]match[/] (regex):");
        AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(rule.Match)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]fix[/] (template):");
        foreach (var line in rule.Fix.TrimEnd().Split('\n'))
            AnsiConsole.MarkupLine($"  {Markup.Escape(line.TrimEnd())}");

        return 0;
    }

    /// <summary>Ids defined in user packs under <c>~/.cifail/rules</c> (empty when none).</summary>
    private static HashSet<string> UserRuleIds()
    {
        var dir = RulePackLoader.DefaultUserRulesDir;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
            return ids;

        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
            foreach (var r in RulePackLoader.ParseDocument(File.ReadAllText(file)))
                ids.Add(r.Id);

        return ids;
    }
}
