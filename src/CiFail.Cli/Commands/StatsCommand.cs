using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CiFail.Cli.Output;
using CiFail.Core.Analysis;
using CiFail.Core.Output;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail stats` — turn accumulated history into signal: top recurring failures, open vs
/// resolved, breakdown by ecosystem, mean time to resolution, and a flaky flag (a failure
/// that was resolved and then came back).
/// </summary>
public sealed partial class StatsCommand : Command<StatsCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
        [CommandOption("--since <WHEN>")]
        [Description("Only count failures since then: a duration (7d, 24h, 2w) or a date (2026-01-01).")]
        public string? Since { get; init; }

        [CommandOption("--repo <REPO_ID>")]
        [Description("Limit to one repository (the root-commit repo id, as recorded with each failure).")]
        public string? Repo { get; init; }

        [CommandOption("--top <N>")]
        [Description("How many top recurring failures to show.")]
        [DefaultValue(10)]
        public int Top { get; init; } = 10;

        [CommandOption("--tests")]
        [Description("Show per-test flakiness instead: the noisiest and flakiest tests from history.")]
        public bool Tests { get; init; }

        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a human report.")]
        public bool Json { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        DateTimeOffset? since;
        try
        {
            since = ParseSince(settings.Since);
        }
        catch (FormatException ex)
        {
            CliConsole.Error(Markup.Escape(ex.Message));
            return ExitCodes.Usage;
        }

        var repo = string.IsNullOrWhiteSpace(settings.Repo) ? null : settings.Repo;

        return StoreSupport.WithStore(settings, store =>
        {
            if (settings.Tests)
                return RunTests(store, settings, since, repo);

            var query = new StatsQuery
            {
                Since = since,
                RepoId = repo,
                Top = settings.Top,
            };

            var stats = StatsService.Compute(store, query);

            if (settings.Json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(StatsJson.ToDto(stats), AnalysisJson.Options));
                return ExitCodes.Ok;
            }

            Render(stats, query);
            return ExitCodes.Ok;
        });
    }

    private static int RunTests(IAnalysisStore store, Settings settings, DateTimeOffset? since, string? repo)
    {
        var query = new TestStatsQuery { Since = since, RepoId = repo, Top = settings.Top };
        var snapshot = TestStatsService.Compute(store, query);

        if (settings.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(TestStatsJson.ToDto(snapshot), AnalysisJson.Options));
            return ExitCodes.Ok;
        }

        RenderTests(snapshot);
        return ExitCodes.Ok;
    }

    private static void RenderTests(TestStatsSnapshot s)
    {
        if (s.TotalTestFailures == 0)
        {
            CliConsole.Hint("[grey]No test failures in history yet. Analyze a JUnit/TRX report " +
                "([bold]cifail analyze --format junit results.xml[/]) to track per-test flakiness.[/]");
            return;
        }

        CliConsole.Out.MarkupLine($"[grey]{s.DistinctTests} tests {Glyphs.Dot} {s.TotalTestFailures} failures {Glyphs.Dot} " +
            $"{s.FlakyCount} flaky.[/] [grey](cifail tracks failures, not passes — this is recurring/flaky, not a pass-rate.)[/]");

        var table = new Table().Border(TableBorder.Rounded).Title("flakiest / noisiest tests");
        table.AddColumn(new TableColumn("fails").RightAligned());
        table.AddColumn(new TableColumn("days").RightAligned());
        table.AddColumn("test");
        table.AddColumn("last seen");
        table.AddColumn("state");
        foreach (var t in s.Tests)
            table.AddRow(
                t.FailureCount.ToString(),
                t.DistinctDays.ToString(),
                Markup.Escape(t.FullName),
                Markup.Escape(t.LastSeen.LocalDateTime.ToString("yyyy-MM-dd")),
                TestStateCell(t));
        CliConsole.Out.Write(table);
    }

    private static string TestStateCell(TestStat t)
    {
        if (t.Flaky) return "[orange1]flaky[/]";
        if (t.OpenCount == 0) return "[green]resolved[/]";
        return t.OpenCount == t.FailureCount ? "[yellow]open[/]" : $"[yellow]{t.OpenCount} open[/]";
    }

    private static void Render(StatsSnapshot s, StatsQuery query)
    {
        if (s.Total == 0)
        {
            CliConsole.Hint("[grey]No failures recorded in that window yet. Run [bold]cifail analyze[/] first.[/]");
            return;
        }

        var scope = query.Since is { } since ? $" since {since.LocalDateTime:yyyy-MM-dd}" : "";
        var repo = query.RepoId is null ? "" : $" {Glyphs.Dot} repo {Short(query.RepoId)}";

        var summary = new Grid().AddColumn().AddColumn();
        void Row(string k, string v) => summary.AddRow($"[grey]{k}[/]", v);
        Row("total failures", s.Total.ToString());
        Row("open / resolved", $"[yellow]{s.Open}[/] / [green]{s.Resolved}[/]");
        Row("unmatched (coverage gaps)", s.Unmatched.ToString());
        Row("recurrence rate", $"{s.RecurrenceRate:P0} [grey]of distinct failures seen >1{Glyphs.Times}[/]");
        Row("mean time to resolve", s.MeanTimeToResolution is { } m
            ? $"{Humanize(m)} [grey](over {s.ResolvedWithTiming})[/]"
            : $"[grey]{Glyphs.Dash}[/]");
        CliConsole.Out.Write(new Panel(summary).Header($"[bold]cifail stats[/][grey]{scope}{repo}[/]").Border(BoxBorder.Rounded));

        if (s.ByEcosystem.Count > 0)
        {
            var eco = new Table().Border(TableBorder.Rounded).Title("by ecosystem");
            eco.AddColumn("ecosystem");
            eco.AddColumn(new TableColumn("count").RightAligned());
            foreach (var c in s.ByEcosystem)
                eco.AddRow(Markup.Escape(c.Key), c.Count.ToString());
            CliConsole.Out.Write(eco);
        }

        var top = new Table().Border(TableBorder.Rounded).Title("top recurring failures");
        top.AddColumn(new TableColumn(Glyphs.Times).RightAligned());
        top.AddColumn("rule");
        top.AddColumn("last seen");
        top.AddColumn("state");
        foreach (var f in s.TopFailures)
            top.AddRow(
                f.Count.ToString(),
                Markup.Escape(f.RuleId),
                Markup.Escape(f.LastSeen.LocalDateTime.ToString("yyyy-MM-dd")),
                StateCell(f));
        CliConsole.Out.Write(top);

        if (s.Flaky.Count > 0)
        {
            CliConsole.Out.WriteLine();
            CliConsole.Out.MarkupLine($"[orange1]{Glyphs.Warning} {s.Flaky.Count} flaky[/] (resolved, then recurred — the fix didn't stick):");
            foreach (var f in s.Flaky)
                CliConsole.Out.MarkupLine($"  [orange1]{Glyphs.Bullet}[/] {Markup.Escape(f.RuleId)} [grey]({f.Count}{Glyphs.Times}, last {f.LastSeen.LocalDateTime:yyyy-MM-dd})[/]");
        }
    }

    private static string StateCell(FingerprintStat f)
    {
        if (f.Flaky) return "[orange1]flaky[/]";
        if (f.OpenCount == 0) return "[green]resolved[/]";
        return f.OpenCount == f.Count ? "[yellow]open[/]" : $"[yellow]{f.OpenCount} open[/]";
    }

    /// <summary>Parse a --since value: a relative duration (7d/24h/2w/30m) or an absolute date.</summary>
    internal static DateTimeOffset? ParseSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        var m = DurationRegex().Match(value);
        if (m.Success)
        {
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups["u"].Value.ToLowerInvariant();
            var span = unit switch
            {
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                "d" => TimeSpan.FromDays(n),
                "w" => TimeSpan.FromDays(7 * n),
                _ => throw new FormatException($"unknown duration unit '{unit}'"),
            };
            return DateTimeOffset.UtcNow - span;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt;

        throw new FormatException($"could not parse --since '{value}' (try 7d, 24h, 2w, or a date like 2026-01-01)");
    }

    private static string Humanize(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{t.TotalDays:0.#}d";
        if (t.TotalHours >= 1) return $"{t.TotalHours:0.#}h";
        if (t.TotalMinutes >= 1) return $"{t.TotalMinutes:0.#}m";
        return $"{t.TotalSeconds:0}s";
    }

    private static string Short(string id) => id.Length >= 10 ? id[..10] : id;

    [GeneratedRegex(@"^(?<n>\d+)\s*(?<u>[mhdwMHDW])$")]
    private static partial Regex DurationRegex();
}
