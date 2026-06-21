#if CIFAIL_SERVER
using System.ComponentModel;
using CiFail.Core.Configuration;
using CiFail.Server;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CiFail.Cli.Commands;

/// <summary>
/// `cifail serve` — run cifail as a shared, long-running HTTP service over the same
/// analysis pipeline + database the CLI uses. Only compiled into the full / Docker build
/// (the <c>CIFAIL_SERVER</c> symbol); the slim binaries never pull in ASP.NET Core.
/// </summary>
public sealed class ServeCommand : Command<ServeCommand.Settings>
{
    public sealed class Settings : StoreSettings
    {
        [CommandOption("-p|--port <PORT>")]
        [Description("Port to listen on.")]
        [DefaultValue(8080)]
        public int Port { get; init; } = 8080;

        [CommandOption("--host <HOST>")]
        [Description("Interface to bind (default 0.0.0.0, all interfaces).")]
        [DefaultValue("0.0.0.0")]
        public string Host { get; init; } = "0.0.0.0";
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Resolve the database the same way every other command does (CLI > env > config > sqlite).
        var database = ConfigLoader.Load(settings.DbProvider, settings.DbConnection).Database;

        AnsiConsole.MarkupLine(
            $"[green]cifail serve[/] listening on [bold]http://{settings.Host}:{settings.Port}[/] " +
            $"(database: [bold]{Markup.Escape(database.Provider)}[/])");

        return CiFailServer.Run(new ServeOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = database,
        });
    }
}
#endif
