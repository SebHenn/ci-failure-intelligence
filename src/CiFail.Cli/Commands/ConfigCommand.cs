using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using CiFail.Cli.Output;
using CiFail.Core;
using CiFail.Core.Ai;
using CiFail.Core.Configuration;
using CiFail.Core.Output;
using CiFail.Core.Rules;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail config` (alias `doctor`) — show how this cifail is actually configured, and check
/// that configuration for problems.
///
/// <para>
/// Every question this answers used to require reading the source: which paths am I using,
/// is this the slim or the full build, which database providers does this binary actually
/// have, did my config.yaml even get read, and is the setting I edited the one taking effect?
/// Each value is shown with its <b>provenance</b> — config file, environment variable, or
/// built-in default — because "I set it and nothing changed" is almost always a precedence
/// surprise.
/// </para>
///
/// <para>
/// <b>Secrets are never printed.</b> Connection strings, webhook URLs, tokens and passwords
/// are reported as set/not-set alongside the name of the variable they come from, so this
/// output is safe to paste into a bug report.
/// </para>
/// </summary>
public sealed class ConfigCommand : Command<ConfigCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a human report.")]
        public bool Json { get; init; }

        [CommandOption("--strict")]
        [Description("Exit non-zero on warnings too, not just errors (for CI).")]
        public bool Strict { get; init; }

        [CommandOption("--path <FILE>")]
        [Description("Inspect this config file instead of ~/.cifail/config.yaml.")]
        public string? Path { get; init; }
    }

    /// <summary>Where a value came from. Mirrors the loader's precedence order.</summary>
    private enum Origin { Default, File, Environment }

    /// <summary>A single reported setting: its effective value, where it came from, and a note.</summary>
    private sealed record Setting(string Name, string Value, Origin Origin, string? Detail = null)
    {
        public string OriginLabel => Origin switch
        {
            Origin.Environment => Detail ?? "environment",
            Origin.File => "config.yaml",
            _ => "default",
        };
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var configPath = string.IsNullOrWhiteSpace(settings.Path) ? CiFailPaths.ConfigPath : settings.Path;

        // The file layer on its own, so a value can be attributed to it rather than to a default
        // that happens to be identical. A broken file is a finding here, not a crash.
        CiFailConfig file;
        IReadOnlyList<ConfigDiagnostic> diagnostics;
        try
        {
            file = ConfigLoader.LoadFile(configPath);
            diagnostics = ConfigValidator.Validate(configPath);
        }
        catch (ConfigException ex)
        {
            file = new CiFailConfig();
            diagnostics = new[]
            {
                new ConfigDiagnostic(DiagnosticSeverity.Error, "", ex.Message, ex.Line),
            };
        }

        var effective = SafeLoad(configPath);
        var report = new Report(configPath, file, effective, diagnostics);

        if (settings.Json)
            CliConsole.Out.WriteLine(JsonSerializer.Serialize(report.ToDto(), AnalysisJson.Options));
        else
            Render(report);

        var errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = diagnostics.Count - errors;
        return errors > 0 || (settings.Strict && warnings > 0) ? ExitCodes.Config : ExitCodes.Ok;
    }

    /// <summary>The fully layered config, falling back to defaults if the file is unreadable.</summary>
    private static CiFailConfig SafeLoad(string path)
    {
        try
        {
            return ConfigLoader.Load(configPath: path);
        }
        catch (ConfigException)
        {
            return new CiFailConfig();
        }
    }

    // ---- the report -------------------------------------------------------------------

    private sealed class Report
    {
        public Report(string configPath, CiFailConfig file, CiFailConfig effective,
            IReadOnlyList<ConfigDiagnostic> diagnostics)
        {
            ConfigPath = configPath;
            ConfigExists = File.Exists(configPath);
            Effective = effective;
            Diagnostics = diagnostics;
            Database = BuildDatabase(file, effective);
            Ai = BuildAi(file, effective);
            Notifications = BuildNotifications(file, effective);

            // Resolved from the same config object that was just reported, so the listed
            // directories are the ones this run would actually load from.
            RuleDirs = RuleSearchPath.Resolve(config: effective);
        }

        public string ConfigPath { get; }
        public bool ConfigExists { get; }
        public CiFailConfig Effective { get; }
        public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }
        public IReadOnlyList<Setting> Database { get; }
        public IReadOnlyList<Setting> Ai { get; }
        public IReadOnlyList<Setting> Notifications { get; }
        public IReadOnlyList<string> RuleDirs { get; }

        public static string Flavor =>
#if CIFAIL_EXTERNAL_DB
            "full";
#else
            "slim";
#endif

        public static string FlavorDetail =>
#if CIFAIL_EXTERNAL_DB
            "external databases + serve";
#else
            "sqlite only; no serve (that ships in the Docker/full build)";
#endif

        public object ToDto() => new
        {
            Version = CliApp.Version,
            Build = Flavor,
            Runtime = RuntimeInformation.FrameworkDescription,
            Platform = RuntimeInformation.RuntimeIdentifier,
            Paths = new
            {
                Home = CiFailPaths.Home,
                HistoryDb = CiFailPaths.HistoryDbPath,
                Config = ConfigPath,
                ConfigExists,
                UserRules = CiFailPaths.UserRulesDir,
                RuleDirs = RuleDirs,
            },
            StoreProviders = StoreRegistry.AvailableNames,
            AiProviders = AiRegistry.AvailableNames,
            Database = Database.Select(ToSettingDto),
            Ai = Ai.Select(ToSettingDto),
            Notifications = Notifications.Select(ToSettingDto),
            Diagnostics = Diagnostics.Select(d => new
            {
                Severity = d.Severity.ToString(),
                d.Path,
                d.Message,
                d.Line,
            }),
        };

        private static object ToSettingDto(Setting s) => new
        {
            s.Name,
            s.Value,
            Source = s.OriginLabel,
        };
    }

    private static IReadOnlyList<Setting> BuildDatabase(CiFailConfig file, CiFailConfig effective)
    {
        var defaults = new CiFailConfig();
        return new List<Setting>
        {
            Resolve("provider", effective.Database.Provider, ConfigLoader.ProviderEnvVar,
                file.Database.Provider, defaults.Database.Provider),

            // The connection string routinely carries a password, so only its presence is shown.
            Secret("connection string", ConfigLoader.ConnectionEnvVar,
                effective.Database.ConnectionString, file.Database.ConnectionString,
                unsetNote: $"using {CiFailPaths.HistoryDbPath}"),

            new("available here", string.Join(", ", StoreRegistry.AvailableNames), Origin.Default,
                Report.Flavor + " build"),
        };
    }

    private static IReadOnlyList<Setting> BuildAi(CiFailConfig file, CiFailConfig effective)
    {
        var defaults = new CiFailConfig();
        var ai = effective.Ai;
        var limits = ai.Limits.Any
            ? string.Join(", ", new[]
            {
                ai.Limits.MaxCallsPerRun > 0 ? $"{ai.Limits.MaxCallsPerRun}/run" : null,
                ai.Limits.MaxCallsPerMinute > 0 ? $"{ai.Limits.MaxCallsPerMinute}/min" : null,
                ai.Limits.MaxRequestChars > 0 ? $"{ai.Limits.MaxRequestChars} chars" : null,
            }.Where(s => s is not null))
            : "unlimited";

        return new List<Setting>
        {
            Resolve("provider", ai.Provider, ConfigLoader.AiProviderEnvVar, file.Ai.Provider, defaults.Ai.Provider),
            Resolve("model", ai.Model ?? "(provider default)", ConfigLoader.AiModelEnvVar,
                file.Ai.Model, defaults.Ai.Model),
            Resolve("base url", ai.BaseUrl ?? "(provider default)", ConfigLoader.AiUrlEnvVar,
                file.Ai.BaseUrl, defaults.Ai.BaseUrl),
            Resolve("embeddings", ai.Embeddings ? "on" : "off", ConfigLoader.AiEmbeddingsEnvVar,
                file.Ai.Embeddings ? "on" : null, "off"),
            Resolve("embedding dims", ai.EmbeddingDimensions.ToString(), ConfigLoader.AiEmbeddingDimsEnvVar,
                file.Ai.EmbeddingDimensions.ToString(), defaults.Ai.EmbeddingDimensions.ToString()),

            // The key itself never appears — only which variable holds it and whether it's set.
            new("api key", EnvState(ai.ApiKeyEnv), Origin.Environment, ai.ApiKeyEnv),

            new("call limits", limits, ai.Limits.Any ? Origin.File : Origin.Default),
        };
    }

    private static IReadOnlyList<Setting> BuildNotifications(CiFailConfig file, CiFailConfig effective)
    {
        var n = effective.Notifications;
        var events = n.Events.Count == 0 ? "all" : string.Join(", ", n.Events);

        var settings = new List<Setting>
        {
            new("events", events, file.Notifications.Events.Count > 0 ? Origin.File : Origin.Default),
            Secret("slack", ConfigLoader.NotifySlackUrlEnvVar, n.SlackWebhookUrl, file.Notifications.SlackWebhookUrl),
            Secret("webhook", ConfigLoader.NotifyWebhookUrlEnvVar, n.WebhookUrl, file.Notifications.WebhookUrl),
            Secret("discord", ConfigLoader.NotifyDiscordUrlEnvVar, n.DiscordWebhookUrl, file.Notifications.DiscordWebhookUrl),
            Secret("teams", ConfigLoader.NotifyTeamsUrlEnvVar, n.TeamsWebhookUrl, file.Notifications.TeamsWebhookUrl),
            new("dedupe window", $"{n.DedupeSeconds}s", n.DedupeSeconds == 300 ? Origin.Default : Origin.File),
        };

        if (n.Smtp is { } smtp)
            settings.Add(new Setting("smtp", $"{smtp.Host}:{smtp.Port} → {smtp.To}", Origin.File,
                $"password from {smtp.PasswordEnv} ({EnvState(smtp.PasswordEnv)})"));

        if (n.GitHub is { } gh)
            settings.Add(new Setting("github issues", gh.Repo, Origin.File,
                $"token from {gh.TokenEnv} ({EnvState(gh.TokenEnv)})"));

        return settings;
    }

    /// <summary>
    /// Attribute an effective value to env &gt; file &gt; default, mirroring
    /// <see cref="ConfigLoader.Load"/>'s precedence so what's reported is what actually happened.
    /// </summary>
    private static Setting Resolve(string name, string value, string envVar, string? fileValue, string? defaultValue)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
            return new Setting(name, value, Origin.Environment, envVar);

        var fromFile = !string.IsNullOrWhiteSpace(fileValue) &&
                       !string.Equals(fileValue, defaultValue, StringComparison.Ordinal);
        return new Setting(name, value, fromFile ? Origin.File : Origin.Default);
    }

    /// <summary>
    /// Report a sensitive value by presence only. This is the invariant that makes the whole
    /// command safe to paste into an issue, so the value is never interpolated anywhere.
    /// </summary>
    private static Setting Secret(string name, string envVar, string? effective, string? fileValue,
        string? unsetNote = null)
    {
        var configured = !string.IsNullOrWhiteSpace(effective);
        var fromEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar));

        var origin = fromEnv ? Origin.Environment : (!string.IsNullOrWhiteSpace(fileValue) ? Origin.File : Origin.Default);
        var value = configured ? "set" : "not set";
        var detail = fromEnv ? envVar : (configured ? null : unsetNote);

        return new Setting(name, value, origin, detail);
    }

    private static string EnvState(string envVar) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)) ? "not set" : "set";

    // ---- rendering --------------------------------------------------------------------

    private static void Render(Report report)
    {
        var console = CliConsole.Out;

        console.MarkupLine($"[bold]cifail {Markup.Escape(CliApp.Version)}[/] " +
            $"[grey]({Markup.Escape(Report.Flavor)} build — {Markup.Escape(Report.FlavorDetail)})[/]");
        console.MarkupLine($"[grey]{Markup.Escape(RuntimeInformation.FrameworkDescription)} on " +
            $"{Markup.Escape(RuntimeInformation.RuntimeIdentifier)}[/]");
        console.WriteLine();

        var paths = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
        void Path(string name, string value, string? note = null) =>
            paths.AddRow($"[grey]{name}[/]", Markup.Escape(value) + (note is null ? "" : $" [grey]({Markup.Escape(note)})[/]"));

        var homeOverridden = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CiFailPaths.HomeEnvVar));
        Path("home", CiFailPaths.Home, homeOverridden ? CiFailPaths.HomeEnvVar : null);
        Path("history db", CiFailPaths.HistoryDbPath, File.Exists(CiFailPaths.HistoryDbPath) ? null : "not created yet");
        Path("config", report.ConfigPath, report.ConfigExists ? null : "not found — using defaults");

        // Every directory rules load from, in load order — a repo's own .cifail/rules and
        // anything added via config.yaml, CIFAIL_RULES or --rules, not just the home one.
        for (int i = 0; i < report.RuleDirs.Count; i++)
            Path(i == 0 ? "rule packs" : "", report.RuleDirs[i], RulesListCommand.PackNote(report.RuleDirs[i]));

        console.Write(new Panel(paths).Header(" paths ").Border(BoxBorder.Rounded));

        Section(console, "database", report.Database);
        Section(console, "ai", report.Ai);
        Section(console, "notifications", report.Notifications);

        RenderDiagnostics(console, report);
    }

    private static void Section(IAnsiConsole console, string title, IReadOnlyList<Setting> settings)
    {
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{title}[/]");
        table.AddColumn("setting");
        table.AddColumn("value");
        table.AddColumn("from");

        foreach (var s in settings)
        {
            var from = s.Origin == Origin.Default
                ? $"[grey]{Markup.Escape(s.OriginLabel)}[/]"
                : Markup.Escape(s.OriginLabel);
            var detail = s.Origin == Origin.Environment || s.Detail is null
                ? ""
                : $" [grey]({Markup.Escape(s.Detail)})[/]";
            table.AddRow(Markup.Escape(s.Name), Markup.Escape(s.Value) + detail, from);
        }

        console.Write(table);
    }

    private static void RenderDiagnostics(IAnsiConsole console, Report report)
    {
        if (report.Diagnostics.Count == 0)
        {
            console.MarkupLine(report.ConfigExists
                ? $"[green]{Glyphs.Check}[/] config.yaml looks good."
                : $"[green]{Glyphs.Check}[/] No config file — cifail works fine without one.");
            return;
        }

        console.WriteLine();
        foreach (var d in report.Diagnostics.OrderBy(d => d.Severity))
        {
            var (tag, colour) = d.Severity == DiagnosticSeverity.Error ? ("error", "red") : ("warning", "yellow");
            var where = d.Line is { } line ? $"line {line}" : d.Path;
            var prefix = string.IsNullOrEmpty(where) ? "" : $"{Markup.Escape(where)}: ";
            console.MarkupLine($"[{colour}]{tag}:[/] {prefix}{Markup.Escape(d.Message)}");
        }
    }

}
