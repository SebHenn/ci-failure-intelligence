using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CiFail.Core.Ai;
using CiFail.Core.Analysis;
using CiFail.Core.Notifications;
using CiFail.Core.Output;
using CiFail.Core.Rules;
using CiFail.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CiFail.Server;

/// <summary>
/// The <c>cifail serve</c> HTTP API: a stateless host over the same
/// <see cref="AnalysisService"/> + <see cref="IAnalysisStore"/> the CLI uses. The server
/// holds no state — all persistence is the external database — so it scales horizontally.
/// Endpoints return the shared <see cref="AnalysisJson"/> / <see cref="StoredAnalysisJson"/>
/// contract, identical to <c>cifail analyze --json</c>.
/// </summary>
public static class CiFailServer
{
    /// <summary>Build and run the server, blocking until shutdown. Returns the exit code.</summary>
    public static int Run(ServeOptions options)
    {
        var app = Build(options);
        app.Run();
        return 0;
    }

    /// <summary>
    /// Build the configured <see cref="WebApplication"/> without starting it (used by tests,
    /// which start it on a random port).
    /// </summary>
    public static WebApplication Build(ServeOptions options)
    {
        var builder = WebApplication.CreateBuilder();

        // mTLS (R20): when a client CA is configured we must terminate TLS ourselves (mutual TLS
        // needs a server cert + client-cert validation), so we drive Kestrel directly instead of
        // UseUrls. Otherwise keep the simple http bind path unchanged.
        if (options.MutualTls)
            ConfigureMutualTls(builder, options);
        else
            builder.WebHost.UseUrls(options.ResolvedUrl);

        // Rule packs are immutable for the process lifetime — load them once.
        builder.Services.AddSingleton(new RuleEngine(RulePackLoader.LoadAll()));

        // A fresh store is opened per request: EF DbContexts and the SQLite repo are not
        // thread-safe, and the server is otherwise stateless. The git reconciler is never
        // wired here — a central server has no working tree (see R11 for the client-driven path).
        Func<IAnalysisStore> storeFactory = () => StoreFactory.Create(options.Database);
        builder.Services.AddSingleton(storeFactory);

        var app = builder.Build();
        UseBearerAuth(app, options.ResolvedTokens());
        MapEndpoints(app, options.Embedder, options.Notifications);
        return app;
    }

    /// <summary>
    /// Configure Kestrel for mutual TLS (R20): serve over HTTPS with the operator's certificate and
    /// require a client certificate that chains to the supplied CA bundle. Validation is enforced at
    /// the TLS layer (the handshake fails for an untrusted/absent client cert), so no request without
    /// a trusted cert ever reaches a handler.
    /// </summary>
    private static void ConfigureMutualTls(WebApplicationBuilder builder, ServeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TlsCertPath))
            throw new InvalidOperationException(
                "mutual TLS requires a server certificate: set --tls-cert (and --tls-password if encrypted).");

        // net8 target: the X509Certificate2(path, password) ctor is the supported loader here.
        var serverCert = new X509Certificate2(options.TlsCertPath, options.TlsCertPassword);
        var trustedCas = LoadCaBundle(options.ClientCaPath!);

        var endpoint = ParseEndpoint(options.ResolvedUrl);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(endpoint.Address, endpoint.Port, listen =>
            {
                listen.UseHttps(serverCert, https =>
                {
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (cert, _, _) => ChainsToTrustedCa(cert, trustedCas);
                });
            });
        });
    }

    /// <summary>Verify a presented client certificate chains to one of the trusted CA roots.</summary>
    private static bool ChainsToTrustedCa(X509Certificate2 clientCert, X509Certificate2Collection trustedCas)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedCas);
        return chain.Build(clientCert);
    }

    private static X509Certificate2Collection LoadCaBundle(string path)
    {
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(path);
        if (collection.Count == 0)
            throw new InvalidOperationException($"no certificates found in client CA bundle '{path}'.");
        return collection;
    }

    private static (IPAddress Address, int Port) ParseEndpoint(string url)
    {
        var uri = new Uri(url);
        var address = uri.Host switch
        {
            "0.0.0.0" or "*" or "+" => IPAddress.Any,
            "localhost" => IPAddress.Loopback,
            _ => IPAddress.TryParse(uri.Host, out var ip) ? ip : IPAddress.Any,
        };
        return (address, uri.Port);
    }

    /// <summary>
    /// Gate every endpoint except <c>/healthz</c> behind a bearer token. Any one of the configured
    /// tokens (R20: a single token plus per-client named tokens) authorizes a request. With no token
    /// configured the server stays open but logs a loud warning (dev convenience). Tokens are compared
    /// in constant time so the comparison can't be used as a timing oracle.
    /// </summary>
    private static void UseBearerAuth(WebApplication app, IReadOnlyList<NamedToken> tokens)
    {
        if (tokens.Count == 0)
        {
            app.Logger.LogWarning(
                "cifail serve is running WITHOUT authentication — anyone who can reach this port " +
                "can read and modify failure history. Set CIFAIL_SERVER_TOKEN (or --token) to require " +
                "a bearer token, and do not expose serve on an untrusted network.");
            return;
        }

        app.Logger.LogInformation("cifail serve requires a bearer token ({Count} configured).", tokens.Count);

        app.Use(async (context, next) =>
        {
            // The probe and the dashboard shell stay open (kubelet has no token; the shell
            // collects one from the user, then calls the protected API with it).
            if (PublicPaths.Contains(context.Request.Path.Value ?? string.Empty))
            {
                await next(context);
                return;
            }

            if (!IsAuthorized(context.Request, tokens))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await context.Response.WriteAsync("unauthorized");
                return;
            }

            await next(context);
        });
    }

    private static bool IsAuthorized(HttpRequest request, IReadOnlyList<NamedToken> tokens)
    {
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var provided = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());

        // Check every token without early-exit so a match never leaks (timing) which one it was.
        var authorized = false;
        foreach (var t in tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(provided, Encoding.UTF8.GetBytes(t.Token)))
                authorized = true;
        }
        return authorized;
    }

    /// <summary>
    /// Public paths served without a token: the liveness probe and the dashboard shell (R12).
    /// The shell is just static HTML — all data it shows comes from the authenticated API, which
    /// it calls with the token the user enters in-page.
    /// </summary>
    private static readonly HashSet<string> PublicPaths =
        new(StringComparer.OrdinalIgnoreCase) { "/healthz", "/", "/index.html" };

    /// <summary>The bundled dashboard HTML, read once from the embedded resource.</summary>
    private static readonly string DashboardHtml = LoadDashboardHtml();

    private static string LoadDashboardHtml()
    {
        var asm = typeof(CiFailServer).Assembly;
        using var stream = asm.GetManifestResourceStream("CiFail.Server.wwwroot.index.html")
            ?? throw new InvalidOperationException("embedded dashboard resource not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void MapEndpoints(WebApplication app, IAiEmbedder? embedder, NotificationDispatcher? notifications)
    {
        // The bundled web dashboard (R12) — a single static page that talks to the API below.
        IResult dashboard() => Results.Content(DashboardHtml, "text/html");
        app.MapGet("/", dashboard);
        app.MapGet("/index.html", dashboard);

        // Liveness/readiness — unauthenticated, used by the Helm probes.
        app.MapGet("/healthz", () => Results.Text("ok"));

        // Analyze a raw log body. ?type= forces the ecosystem, ?source= names it in history,
        // ?noHistory=1 skips persistence.
        app.MapPost("/analyze", async (HttpRequest request, RuleEngine engine, Func<IAnalysisStore> stores) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest("empty log body");

            var options = new AnalysisOptions
            {
                EcosystemOverride = request.Query["type"].FirstOrDefault(),
                RecordHistory = !ParseBool(request.Query["noHistory"].FirstOrDefault()),
            };
            var source = request.Query["source"].FirstOrDefault() ?? "upload";

            using var store = stores();
            var service = new AnalysisService(engine, store, git: null, ai: null, embedder: embedder);
            var analysis = service.Analyze(source, body, options);

            // Notify (R13): a never-before-seen fingerprint is "new", otherwise a recurrence.
            if (notifications is { HasNotifiers: true } && analysis.HistoryId is { } savedId
                && store.GetById(savedId) is { } stored)
            {
                var prior = store is IFingerprintCounter counter
                    ? counter.CountByFingerprint(stored.Fingerprint)
                    : 1;
                notifications.Dispatch(new Notification(
                    prior > 1 ? NotificationEvent.Recurrence : NotificationEvent.NewFailure, stored));
            }

            return Json(AnalysisJson.Serialize(analysis));
        });

        // Recent history, newest first.
        app.MapGet("/history", (int? limit, Func<IAnalysisStore> stores) =>
        {
            using var store = stores();
            var records = store.GetRecent(limit is > 0 ? limit.Value : 20);
            var dtos = records.Select(StoredAnalysisJson.ToDto).ToList();
            return Json(JsonSerializer.Serialize(dtos, AnalysisJson.Options));
        });

        // Aggregate stats over history (R16). Filters: ?since=<ISO-8601>&repo=<id>&top=N.
        // Uses the store's IAnalysisStats when available, else an in-app fallback — identical
        // numbers either way (see StatsService / StatsComputer).
        app.MapGet("/stats", (HttpRequest request, Func<IAnalysisStore> stores) =>
        {
            DateTimeOffset? since = null;
            if (DateTimeOffset.TryParse(request.Query["since"].FirstOrDefault(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                since = parsed;

            var query = new StatsQuery
            {
                Since = since,
                RepoId = request.Query["repo"].FirstOrDefault() is { Length: > 0 } r ? r : null,
                Top = int.TryParse(request.Query["top"].FirstOrDefault(), out var t) && t > 0 ? t : 10,
            };

            using var store = stores();
            var stats = StatsService.Compute(store, query);
            return Json(JsonSerializer.Serialize(StatsJson.ToDto(stats), AnalysisJson.Options));
        });

        // Failure clusters over history (R25). Filters: ?threshold=0.5&since=<ISO-8601>&repo=<id>&top=N&all=1.
        // Uses the store's IClusterer when available, else an in-app fallback — identical either way
        // (see ClusterService / FailureClusterer).
        app.MapGet("/clusters", (HttpRequest request, Func<IAnalysisStore> stores) =>
        {
            DateTimeOffset? since = null;
            if (DateTimeOffset.TryParse(request.Query["since"].FirstOrDefault(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                since = parsed;

            var query = new ClusterQuery
            {
                Threshold = double.TryParse(request.Query["threshold"].FirstOrDefault(),
                    System.Globalization.CultureInfo.InvariantCulture, out var th) && th is >= 0 and <= 1 ? th : 0.5,
                Since = since,
                RepoId = request.Query["repo"].FirstOrDefault() is { Length: > 0 } r ? r : null,
                Top = int.TryParse(request.Query["top"].FirstOrDefault(), out var t) && t >= 0 ? t : 10,
                IncludeSingletons = request.Query["all"].FirstOrDefault() is "1" or "true",
            };

            using var store = stores();
            var clusters = ClusterService.Compute(store, query);
            return Json(JsonSerializer.Serialize(ClustersJson.ToDto(clusters), AnalysisJson.Options));
        });

        // A single record, or 404.
        app.MapGet("/history/{id:long}", (long id, Func<IAnalysisStore> stores) =>
        {
            using var store = stores();
            var record = store.GetById(id);
            return record is null
                ? Results.NotFound()
                : Json(JsonSerializer.Serialize(StoredAnalysisJson.ToDto(record), AnalysisJson.Options));
        });

        // Open (unresolved) failures for one repository — the client-side reconciler (R11)
        // reads these, then writes auto-resolutions back via /resolve?source=auto.
        app.MapGet("/repos/{repoId}/open", (string repoId, Func<IAnalysisStore> stores) =>
        {
            using var store = stores();
            var dtos = store.GetOpenFailures(repoId).Select(StoredAnalysisJson.ToDto).ToList();
            return Json(JsonSerializer.Serialize(dtos, AnalysisJson.Options));
        });

        // Resolution. Body: { "note": "..." }. Manual by default; ?source=auto&commit=<sha>
        // records a git-correlated auto-resolution (which never overwrites a manual one — the
        // store enforces that, so a no-op returns 404 and the reconciler simply skips it).
        // Returns the updated record, or 404.
        app.MapPost("/resolve/{id:long}", async (long id, HttpRequest request, Func<IAnalysisStore> stores) =>
        {
            ResolveRequest? req;
            try { req = await request.ReadFromJsonAsync<ResolveRequest>(); }
            catch (JsonException) { return Results.BadRequest("invalid JSON body"); }

            if (req is null || string.IsNullOrWhiteSpace(req.Note))
                return Results.BadRequest("note is required");

            var isAuto = string.Equals(request.Query["source"].FirstOrDefault(), "auto", StringComparison.OrdinalIgnoreCase);
            var commit = request.Query["commit"].FirstOrDefault();
            if (isAuto && string.IsNullOrWhiteSpace(commit))
                return Results.BadRequest("commit is required for an auto resolution");

            using var store = stores();
            var ok = isAuto
                ? store.SetAutoResolution(id, commit!, req.Note)
                : store.SetResolution(id, req.Note);
            if (!ok)
                return Results.NotFound();

            var updated = store.GetById(id)!;
            notifications?.Dispatch(new Notification(NotificationEvent.Resolved, updated));
            return Json(JsonSerializer.Serialize(StoredAnalysisJson.ToDto(updated), AnalysisJson.Options));
        });
    }

    private static IResult Json(string json) => Results.Text(json, "application/json");

    private static bool ParseBool(string? v) =>
        v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private sealed record ResolveRequest(string Note);
}
