using System.ComponentModel;
using System.Net;
using CiFail.Cli.Output;
using CiFail.Core.Configuration;
using CiFail.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// Shared options for commands that touch the history store. Lets any of them point at
/// a different database without repeating the flags. Precedence (handled by the config
/// loader): these CLI flags &gt; env (CIFAIL_DB_PROVIDER/CONNECTION) &gt; config.yaml &gt;
/// the SQLite default.
/// </summary>
public class StoreSettings : OutputSettings
{
    [CommandOption("--db-provider <PROVIDER>")]
    [Description("Database backend: sqlite (default) or http. postgres/mysql/sqlserver/mongodb need the Docker/full build; `cifail config` lists what this binary has.")]
    public string? DbProvider { get; init; }

    [CommandOption("--db-connection <CONNECTION>")]
    [Description("Connection string for the chosen database (native format for that engine).")]
    public string? DbConnection { get; init; }

    [CommandOption("--server <URL>")]
    [Description("Talk to a remote cifail serve instance instead of a database (e.g. http://cifail:8080).")]
    public string? Server { get; init; }

    [CommandOption("--server-token <TOKEN>")]
    [Description("Bearer token for --server (falls back to CIFAIL_SERVER_TOKEN).")]
    public string? ServerToken { get; init; }
}

/// <summary>Helper to open the configured store, reporting a friendly error on failure.</summary>
public static class StoreSupport
{
    /// <summary>
    /// Build the store from settings/env/config. Returns null (after printing a clear
    /// message to stderr) when the requested provider isn't available in this build or the
    /// connection fails.
    ///
    /// <para>
    /// Prefer <see cref="WithStore"/>: this only covers failures while <i>opening</i> the store.
    /// Anything that goes wrong while using it — an unreachable server, a rejected token, a
    /// database that disappears mid-query — escapes to the caller.
    /// </para>
    /// </summary>
    public static IAnalysisStore? TryCreate(StoreSettings settings)
    {
        try
        {
            return Create(settings);
        }
        catch (Exception ex)
        {
            ReportOpenFailure(ex);
            return null;
        }
    }

    /// <summary>
    /// Open the store, run <paramref name="body"/> against it, and dispose it — turning every
    /// store failure into a clear message and a <see cref="ExitCodes.StoreUnavailable"/> exit
    /// instead of a stack trace.
    ///
    /// <para>
    /// This is the wrapper every store-backed command should use. Without it a typo'd
    /// <c>--server</c> or an expired token surfaced as a raw .NET exception dump, because the
    /// failure happens during the <i>query</i>, long after the store was successfully created.
    /// </para>
    /// </summary>
    public static int WithStore(StoreSettings settings, Func<IAnalysisStore, int> body)
    {
        IAnalysisStore store;
        try
        {
            store = Create(settings);
        }
        catch (Exception ex)
        {
            ReportOpenFailure(ex);
            return OpenFailureCode(ex);
        }

        using (store)
        {
            try
            {
                return body(store);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ReportServerFailure(settings, ex);
                return ExitCodes.StoreUnavailable;
            }
            catch (ConfigException ex)
            {
                CliConsole.Error($"{Markup.Escape(ex.Location)}: {Markup.Escape(ex.Message)}");
                return ExitCodes.Config;
            }
        }
    }

    /// <summary>
    /// Resolve the settings to a store. <c>--server</c> is shorthand for the remote http
    /// provider and wins over <c>--db-*</c>, so one flag points every read/resolve command at a
    /// shared service. The token (CLI &gt; env) becomes a bearer header when the server
    /// requires auth (R9).
    /// </summary>
    private static IAnalysisStore Create(StoreSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Server))
            return new HttpAnalysisStore(settings.Server, ResolveToken(settings));

        return StoreFactory.Create(settings.DbProvider, settings.DbConnection);
    }

    /// <summary>The bearer token for <c>--server</c>: the flag if given, else the env var.</summary>
    public static string? ResolveToken(StoreSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ServerToken)
            ? settings.ServerToken
            : Environment.GetEnvironmentVariable(HttpAnalysisStore.TokenEnvVar);

    private static void ReportOpenFailure(Exception ex)
    {
        // The debug switch promises a stack trace for any error, including the ones handled
        // nicely here.
        CliConsole.DumpIfDebug(ex);

        switch (ex)
        {
            case StoreProviderNotAvailableException:
                CliConsole.Error(Markup.Escape(ex.Message));
                break;

            case ConfigException config:
                CliConsole.Error($"{Markup.Escape(config.Location)}: {Markup.Escape(config.Message)}");
                break;

            // A typo'd --server is the user's mistake, not a database that won't open, so say so
            // in those terms.
            case UriFormatException or ArgumentException:
                CliConsole.Error($"not a usable server URL — {Markup.Escape(ex.Message)}");
                CliConsole.Hint("[grey]--server takes a base URL, e.g. [bold]http://cifail:8080[/].[/]");
                break;

            default:
                CliConsole.Error($"could not open the database: {Markup.Escape(ex.Message)}");
                break;
        }
    }

    private static int OpenFailureCode(Exception ex) => ex switch
    {
        // A broken config.yaml surfaces here, because the loader runs while resolving the
        // provider — that's a config problem, not an unavailable store.
        ConfigException => ExitCodes.Config,
        UriFormatException or ArgumentException => ExitCodes.Usage,
        _ => ExitCodes.StoreUnavailable,
    };

    /// <summary>
    /// Explain a remote failure in terms of the fix. A 401 in particular is worth naming: the
    /// server is reachable and the request is fine, the client just has no valid token.
    /// </summary>
    private static void ReportServerFailure(StoreSettings settings, Exception ex)
    {
        CliConsole.DumpIfDebug(ex);

        var target = string.IsNullOrWhiteSpace(settings.Server) ? "the cifail server" : settings.Server;

        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
        {
            CliConsole.Error($"{Markup.Escape(target)} rejected the request (401 Unauthorized).");
            CliConsole.Hint($"[grey]Pass [bold]--server-token <TOKEN>[/] or set " +
                $"[bold]{HttpAnalysisStore.TokenEnvVar}[/].[/]");
            return;
        }

        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
        {
            CliConsole.Error($"{Markup.Escape(target)} refused the request (403 Forbidden) — " +
                "the token is recognized but not allowed to do this.");
            return;
        }

        CliConsole.Error($"could not reach {Markup.Escape(target)} — {Markup.Escape(ex.Message)}");
    }
}
