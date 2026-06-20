using CiFail.Core.Rules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>`cifail rules list` — show all loaded rule packs (embedded + user).</summary>
public sealed class RulesListCommand : Command<RulesListCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var rules = RulePackLoader.LoadAll();

        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]rules[/] [grey]({rules.Count} loaded)[/]");
        table.AddColumn("id");
        table.AddColumn("ecosystem");
        table.AddColumn("category");
        table.AddColumn(new TableColumn("conf").RightAligned());
        table.AddColumn("title");

        foreach (var r in rules.OrderBy(r => r.Ecosystem).ThenBy(r => r.Id))
        {
            table.AddRow(
                Markup.Escape(r.Id),
                Markup.Escape(r.Ecosystem),
                Markup.Escape(r.Category),
                $"{r.Confidence:0.00}",
                Markup.Escape(r.Title));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[grey]user rule packs: {Markup.Escape(RulePackLoader.DefaultUserRulesDir)}[/]");
        return 0;
    }
}
