using System.ComponentModel;
using CiFail.Cli.Output;
using CiFail.Core;
using CiFail.Core.Output;
using CiFail.Core.Rules;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>`cifail rules list` — show all loaded rule packs (embedded + user).</summary>
public sealed class RulesListCommand : Command<RulesListCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("--rules <DIR>")]
        [Description("Extra directory of rule packs (repeatable). Adds to ~/.cifail/rules and .cifail/rules.")]
        public string[] Rules { get; init; } = Array.Empty<string>();

        [CommandOption("--json")]
        [Description("Print the rule inventory as JSON instead of a table.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var searchPath = RuleSearchPath.Resolve(settings.Rules);
        var rules = RulePackLoader.LoadFrom(searchPath);

        if (settings.Json)
        {
            // The search path travels with the inventory: "which rules do I have" and "where did
            // they come from" are the same question when a repo ships its own packs.
            Console.Out.WriteLine(RulesJson.Serialize(
                rules.OrderBy(r => r.Ecosystem).ThenBy(r => r.Id), searchPath));
            return ExitCodes.Ok;
        }

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

        CliConsole.Out.Write(table);

        // Which directories were consulted, and which of them actually held packs — "my rule
        // isn't listed" is nearly always a path question, and this answers it without guessing.
        CliConsole.Hint("[grey]user rule packs are loaded from, in order:[/]");
        foreach (var dir in searchPath)
            CliConsole.Hint($"[grey]  {Markup.Escape(dir)} ({Markup.Escape(PackNote(dir))})[/]");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// How many packs a search-path directory holds, in words, for the reader who is asking
    /// why their rule isn't listed. A missing home directory is normal (cifail creates it when
    /// something writes there); a missing directory you asked for is not.
    /// </summary>
    internal static string PackNote(string dir)
    {
        if (!Directory.Exists(dir))
            return string.Equals(dir, CiFailPaths.UserRulesDir, StringComparison.OrdinalIgnoreCase)
                ? "none yet"
                : "not there";

        try
        {
            var count = Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories).Count();
            return count == 0 ? "no packs" : $"{count} pack{(count == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }
}
