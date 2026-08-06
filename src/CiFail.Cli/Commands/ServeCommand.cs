#if CIFAIL_SERVER
using System.ComponentModel;
using CiFail.Cli.Output;
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

        [CommandOption("--tokens-file <PATH>")]
        // Spectre renders option descriptions as MARKUP, so a literal '[' opens a style tag:
        // the unescaped "[name]" here made `cifail serve --help` die with "Could not find
        // color or style 'name'". '[[' is the escape and renders as a single '['.
        [Description("File of per-client tokens (one '<token> [[name]]' per line). Combined with --token and CIFAIL_SERVER_TOKENS.")]
        public string? TokensFile { get; init; }

        [CommandOption("--client-ca <PEM>")]
        [Description("Require mutual TLS: clients must present a cert chaining to this PEM CA bundle. Needs --tls-cert.")]
        public string? ClientCa { get; init; }

        [CommandOption("--tls-cert <PFX>")]
        [Description("Server TLS certificate (PKCS#12/PFX), used with --client-ca for mutual TLS.")]
        public string? TlsCert { get; init; }

        [CommandOption("--tls-password <PASSWORD>")]
        [Description("Password for --tls-cert, if the PFX is encrypted.")]
        public string? TlsPassword { get; init; }
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

        // Per-client named tokens (R20): a CIFAIL_SERVER_TOKENS comma list plus an optional file.
        var tokens = new List<NamedToken>();
        tokens.AddRange(ServerTokens.ParseList(Environment.GetEnvironmentVariable("CIFAIL_SERVER_TOKENS")));
        if (!string.IsNullOrWhiteSpace(settings.TokensFile))
        {
            if (!File.Exists(settings.TokensFile))
            {
                CliConsole.Error($"tokens file not found: {Markup.Escape(settings.TokensFile)}");
                return ExitCodes.Usage;
            }
            tokens.AddRange(ServerTokens.ParseFile(settings.TokensFile));
        }

        // mTLS (R20): a client CA implies mutual TLS, which requires a server cert.
        if (!string.IsNullOrWhiteSpace(settings.ClientCa) && string.IsNullOrWhiteSpace(settings.TlsCert))
        {
            CliConsole.Error("--client-ca requires --tls-cert (mutual TLS needs a server certificate).");
            return ExitCodes.Usage;
        }

        // Optional vector embeddings (R10): only when opted in (ai.embeddings / CIFAIL_AI_EMBEDDINGS).
        var embedder = BuildEmbedder(config.Ai);

        // Optional outbound notifications (R13): built only when a channel is configured.
        var notifications = NotificationDispatcher.FromConfig(config.Notifications);

        var tokenCount = (string.IsNullOrWhiteSpace(token) ? 0 : 1) + tokens.Count;
        var auth = tokenCount == 0
            ? "[yellow]auth: off[/]"
            : $"[green]auth: {tokenCount} bearer token(s)[/]";
        var mtls = !string.IsNullOrWhiteSpace(settings.ClientCa) ? "[green]mTLS: required[/]" : "mTLS: off";
        var scheme = !string.IsNullOrWhiteSpace(settings.ClientCa) ? "https" : "http";
        var sim = embedder is null ? "tf-idf" : $"embeddings:{Markup.Escape(config.Ai.Provider)}";
        var notify = notifications is { HasNotifiers: true } ? "[green]notifications: on[/]" : "notifications: off";
        CliConsole.Out.MarkupLine(
            $"[green]cifail serve[/] listening on [bold]{scheme}://{settings.Host}:{settings.Port}[/] " +
            $"(database: [bold]{Markup.Escape(database.Provider)}[/], {auth}, {mtls}, similarity: {sim}, {notify})");

        return CiFailServer.Run(new ServeOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Database = database,
            AuthToken = token,
            AuthTokens = tokens,
            ClientCaPath = settings.ClientCa,
            TlsCertPath = settings.TlsCert,
            TlsCertPassword = settings.TlsPassword,
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
                CliConsole.Warn($"the '{Markup.Escape(ai.Provider)}' AI provider has no embeddings; " +
                    "falling back to TF-IDF similarity.");
            return embedder;
        }
        catch (Exception ex)
        {
            CliConsole.Warn($"embeddings disabled — {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
#endif
