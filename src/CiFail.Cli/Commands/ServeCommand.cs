#if CIFAIL_SERVER
using System.ComponentModel;
using CiFail.Core.Ai;
using CiFail.Core.Configuration;
using CiFail.Core.Notifications;
using CiFail.Core.Storage;
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

        [CommandOption("--token <TOKEN>")]
        [Description("Require this bearer token on every request except /healthz. Falls back to CIFAIL_SERVER_TOKEN.")]
        public string? Token { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Resolve config the same way every other command does (CLI > env > config > defaults).
        var config = ConfigLoader.Load(settings.DbProvider, settings.DbConnection);
        var database = config.Database;

        // Token precedence: --token > CIFAIL_SERVER_TOKEN. Empty => open (with a warning at startup).
        var token = !string.IsNullOrWhiteSpace(settings.Token)
            ? settings.Token
            : Environment.GetEnvironmentVariable(HttpAnalysisStore.TokenEnvVar);

        // Optional vector embeddings (R10): only when opted in (ai.embeddings / CIFAIL_AI_EMBEDDINGS).
        var embedder = BuildEmbedder(config.Ai);

        // Optional outbound notifications (R13): built only when a channel is configured.
        var notifications = NotificationDispatcher.FromConfig(config.Notifications);

        var auth = string.IsNullOrWhiteSpace(token)
            ? "[yellow]auth: off[/]"
            : "[green]auth: bearer token[/]";
        var sim = embedder is null ? "tf-idf" : $"embeddings:{Markup.Escape(config.Ai.Provider)}";
        var notify = notifications is { HasNotifiers: true } ? "[green]notifications: on[/]" : "notifications: off";
        AnsiConsole.MarkupLine(
            $"[green]cifail serve[/] listening on [bold]http://{settings.Host}:{settings.Port}[/] " +
            $"(database: [bold]{Markup.Escape(database.Provider)}[/], {auth}, similarity: {sim}, {notify})");

        return CiFailServer.Run(new ServeOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = database,
            AuthToken = token,
            Embedder = embedder,
            Notifications = notifications,
        });
    }

    private static IAiEmbedder? BuildEmbedder(AiConfig ai)
    {
        if (!ai.Embeddings) return null;
        try
        {
            var embedder = AiFactory.CreateEmbedder(ai);
            if (embedder is null)
                AnsiConsole.MarkupLine(
                    $"[yellow]warning:[/] the '{Markup.Escape(ai.Provider)}' AI provider has no embeddings; " +
                    "falling back to TF-IDF similarity.");
            return embedder;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]warning:[/] embeddings disabled — {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
#endif
