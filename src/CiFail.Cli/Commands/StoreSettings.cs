using System.ComponentModel;
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
public class StoreSettings : CommandSettings
{
    [CommandOption("--db-provider <PROVIDER>")]
    [Description("Database backend: sqlite (default), postgres, mysql, sqlserver, mongodb.")]
    public string? DbProvider { get; init; }

    [CommandOption("--db-connection <CONNECTION>")]
    [Description("Connection string for the chosen database (native format for that engine).")]
    public string? DbConnection { get; init; }

    [CommandOption("--server <URL>")]
    [Description("Talk to a remote cifail serve instance instead of a database (e.g. http://cifail:8080).")]
    public string? Server { get; init; }
}

/// <summary>Helper to open the configured store, reporting a friendly error on failure.</summary>
public static class StoreSupport
{
    /// <summary>
    /// Build the store from settings/env/config. Returns null (after printing a clear
    /// message) when the requested provider isn't available in this build or the
    /// connection fails, so the caller can exit with code 2.
    /// </summary>
    public static IAnalysisStore? TryCreate(StoreSettings settings)
    {
        try
        {
            // --server is shorthand for the remote http provider; it wins over --db-* so a
            // single flag points the read/resolve commands at a shared service.
            if (!string.IsNullOrWhiteSpace(settings.Server))
                return StoreFactory.Create("http", settings.Server);

            return StoreFactory.Create(settings.DbProvider, settings.DbConnection);
        }
        catch (StoreProviderNotAvailableException ex)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] could not open the database: {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
