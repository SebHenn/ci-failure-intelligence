using System.Reflection;
using System.Text;
using CiFail.Cli.Commands;
using CiFail.Cli.Output;
using CiFail.Core.Ai;
using CiFail.Core.Configuration;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli;

/// <summary>
/// Builds and runs the cifail command line. Split out of <c>Program.cs</c> so tests can
/// construct the same app the real entry point does, and so the top-level exception handler
/// has somewhere to live.
/// </summary>
public static class CliApp
{
    /// <summary>The entry point: prepare the console, build the app, run it.</summary>
    public static int Run(string[] args)
    {
        ConfigureConsole();
        return Build().Run(args);
    }

    /// <summary>
    /// Switch the console to UTF-8 so glyphs, and any non-ASCII in the logs themselves, survive.
    ///
    /// <para>
    /// This matters just as much when output is <i>redirected</i>: on Windows a redirected stream
    /// otherwise inherits the OEM code page, which silently mangles every non-ASCII character on
    /// its way into a piped SARIF/Markdown report. The one thing that must not happen is a BOM
    /// appearing at the head of that pipe, so this uses <see cref="UTF8Encoding"/> with the
    /// identifier off rather than <see cref="Encoding.UTF8"/> (whose preamble .NET would emit).
    /// </para>
    ///
    /// <para>
    /// All of it is best-effort. A console that refuses the switch just means
    /// <see cref="Glyphs"/> falls back to ASCII — never a failed analysis.
    /// </para>
    /// </summary>
    public static void ConfigureConsole()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            Console.OutputEncoding = utf8;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException
            or System.Security.SecurityException)
        {
            // Every documented failure of these setters (no console attached, a platform that
            // fixes the encoding from the locale, a restricted host) is fine to ignore.
        }

        try
        {
            // Logs piped in on stdin are UTF-8 in practice; decoding them as OEM corrupts the
            // very lines the rules need to match.
            Console.InputEncoding = utf8;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException
            or System.Security.SecurityException)
        {
            // Every documented failure of these setters (no console attached, a platform that
            // fixes the encoding from the locale, a restricted host) is fine to ignore.
        }
    }

    /// <summary>The configured <see cref="CommandApp"/>, ready to <c>Run</c>.</summary>
    /// <param name="console">
    /// Where Spectre writes what it renders itself — help text, <c>--version</c>, usage errors.
    /// Null (the default) means the ambient console. Tests pass their own, because these go
    /// through Spectre's console rather than <see cref="CliConsole"/> and would otherwise escape
    /// capture.
    /// </param>
    public static CommandApp Build(IAnsiConsole? console = null)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("cifail");
            config.SetApplicationVersion(Version);
            if (console is not null) config.Settings.Console = console;

            // Without this, any exception escaping a command reaches the user as a Spectre
            // stack trace and an opaque exit code.
            config.SetExceptionHandler(HandleException);

            config.AddCommand<AnalyzeCommand>("analyze")
                .WithDescription("Read a build/test log and explain what broke and how to fix it.")
                .WithExample("analyze", "build.log")
                .WithExample("analyze", "--json", "test.log")
                .WithExample("analyze", "--type", "dotnet", "build.log");

            config.AddCommand<GateCommand>("gate")
                .WithDescription("Fail CI on a new failure only, ignoring the backlog in .cifail/baseline.txt.")
                .WithExample("gate", "build.log")
                .WithExample("gate", "--update", "build.log");

            config.AddCommand<HistoryCommand>("history")
                .WithDescription("List the failures you've analyzed before (or show one by its number).")
                .WithExample("history")
                .WithExample("history", "42");

            config.AddCommand<StatsCommand>("stats")
                .WithDescription("Show trends across your history: top failures, flaky tests, resolution times.")
                .WithExample("stats")
                .WithExample("stats", "--since", "7d", "--top", "5");

            config.AddCommand<ClustersCommand>("clusters")
                .WithDescription("Group near-duplicate failures from history so you see distinct root causes.")
                .WithExample("clusters")
                .WithExample("clusters", "--threshold", "0.6", "--since", "30d");

            config.AddCommand<ResolveCommand>("resolve")
                .WithDescription("Save how you fixed a failure, so cifail can remind you next time.")
                .WithExample("resolve", "42", "--note", "\"Fixed the package name typo\"");

            config.AddCommand<SuggestRuleCommand>("suggest-rule")
                .WithDescription("Draft a new rule (with AI) for a log no rule matches, validated before you save it.")
                .WithExample("suggest-rule", "build.log")
                .WithExample("suggest-rule", "build.log", "--write");

            config.AddCommand<ReconcileCommand>("reconcile")
                .WithDescription("Auto-resolve past failures that no longer happen at the current commit.")
                .WithExample("reconcile");

            config.AddCommand<PruneCommand>("prune")
                .WithDescription("Delete old analyses from history.")
                .WithExample("prune", "--older-than", "90d", "--dry-run");

            config.AddCommand<InitCommand>("init")
                .WithDescription("Install git hooks so cifail auto-resolves fixed failures on each commit.")
                .WithExample("init");

            config.AddCommand<ConfigCommand>("config")
                .WithAlias("doctor")
                .WithDescription("Show how cifail is configured and check it for problems.")
                .WithExample("config")
                .WithExample("config", "--json");

#if CIFAIL_SERVER
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run cifail as a shared HTTP service (full/Docker build only).")
                .WithExample("serve", "--port", "8080");
#endif

            config.AddBranch("rules", rules =>
            {
                rules.SetDescription("See and author the failure patterns cifail knows about.");
                rules.AddCommand<RulesListCommand>("list")
                    .WithDescription("List every pattern cifail can recognize.");
                rules.AddCommand<RulesTestCommand>("test")
                    .WithDescription("Try a regex against a log and show its matches + captures.")
                    .WithExample("rules", "test", "\"error NU(?<code>\\d+)\"", "--file", "build.log");
                rules.AddCommand<RulesValidateCommand>("validate")
                    .WithDescription("Lint rule packs; exits non-zero on any error (CI-friendly).")
                    .WithExample("rules", "validate", "src/CiFail.Core/rulepacks");
                rules.AddCommand<RulesExplainCommand>("explain")
                    .WithDescription("Show one rule's full definition and where it came from.")
                    .WithExample("rules", "explain", "nuget-nu1101");
            });
        });

        return app;
    }

    /// <summary>The version stamped by <c>Directory.Build.props</c>, e.g. <c>0.2.0</c>.</summary>
    public static string Version =>
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliApp).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    /// Turn anything that escapes a command into a one-line stderr message and a meaningful
    /// exit code (see <see cref="ExitCodes"/>). <c>CIFAIL_DEBUG=1</c> prints the stack trace
    /// instead, for when the friendly message isn't enough.
    /// </summary>
    private static int HandleException(Exception ex, ITypeResolver? resolver)
    {
        if (CliConsole.IsDebug)
        {
            CliConsole.DumpIfDebug(ex);
            return ExitCodeFor(ex);
        }

        switch (ex)
        {
            // Bad invocation. Spectre already renders these with the offending token marked,
            // so keep its rendering — just move it to stderr, where diagnostics belong.
            case CommandAppException { Pretty: { } pretty }:
                CliConsole.Err.Write(pretty);
                CliConsole.Hint("[grey]Run [bold]cifail --help[/] to see the available commands.[/]");
                return ExitCodes.Usage;

            case CommandAppException:
                CliConsole.Error(Markup.Escape(ex.Message));
                CliConsole.Hint("[grey]Run [bold]cifail --help[/] to see the available commands.[/]");
                return ExitCodes.Usage;

            case ConfigException config:
                CliConsole.Error($"{Markup.Escape(config.Location)}: {Markup.Escape(config.Message)}");
                CliConsole.Hint("[grey]Check it with [bold]cifail config[/].[/]");
                return ExitCodes.Config;

            case StoreProviderNotAvailableException or AiProviderNotAvailableException:
                CliConsole.Error(Markup.Escape(ex.Message));
                return ExitCodeFor(ex);

            case OperationCanceledException:
                return ExitCodes.Canceled;

            case HttpRequestException:
                CliConsole.Error($"could not reach the cifail server — {Markup.Escape(ex.Message)}");
                return ExitCodes.StoreUnavailable;

            case UnauthorizedAccessException:
                CliConsole.Error($"permission denied — {Markup.Escape(ex.Message)}");
                return ExitCodes.Usage;

            case IOException:
                CliConsole.Error(Markup.Escape(ex.Message));
                return ExitCodes.Usage;

            default:
                CliConsole.Error($"{Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}");
                CliConsole.Hint($"[grey]This looks like a bug. Set [bold]{CliConsole.DebugEnvVar}=1[/] for the full stack " +
                    "trace, and please report it at https://github.com/SebHenn/ci-failure-intelligence/issues[/]");
                return ExitCodes.Internal;
        }
    }

    private static int ExitCodeFor(Exception ex) => ex switch
    {
        CommandAppException => ExitCodes.Usage,
        ConfigException => ExitCodes.Config,
        StoreProviderNotAvailableException => ExitCodes.StoreUnavailable,
        AiProviderNotAvailableException => ExitCodes.DependencyUnavailable,
        OperationCanceledException => ExitCodes.Canceled,
        HttpRequestException => ExitCodes.StoreUnavailable,
        UnauthorizedAccessException or IOException => ExitCodes.Usage,
        _ => ExitCodes.Internal,
    };
}
