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
using CiFail.Server.Dashboard;
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

        // The C#-rendered dashboard (R28) is Blazor static SSR — register the component services.
        builder.Services.AddRazorComponents();

        // A fresh store is opened per request: EF DbContexts and the SQLite repo are not
        // thread-safe, and the server is otherwise stateless. The git reconciler is never
        // wired here — a central server has no working tree (see R11 for the client-driven path).
        Func<IAnalysisStore> storeFactory = () => StoreFactory.Create(options.Database);
        builder.Services.AddSingleton(storeFactory);

        var app = builder.Build();
        UseBearerAuth(app, options.ResolvedTokens());
        // Required by MapRazorComponents; it only validates endpoints that opt in (Blazor form
        // posts), so the plain minimal-API form posts below (/login, /ui/resolve) are untouched.
        app.UseAntiforgery();
        MapEndpoints(app, options.ResolvedTokens(), options.Embedder, options.Notifications);
        app.MapRazorComponents<App>();
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
            // The probe and the sign-in flow stay open (kubelet has no token; a signing-in
            // browser has no cookie yet — /ui/login validates the submitted token itself).
            if (PublicPaths.Contains(context.Request.Path.Value ?? string.Empty))
            {
                await next(context);
                return;
            }

            if (!IsAuthorized(context.Request, tokens))
            {
                // A browser navigating to a protected page gets bounced to the sign-in page;
                // a programmatic/API client gets a plain 401 with the Bearer challenge.
                if (WantsHtml(context.Request))
                {
                    context.Response.Redirect("/login");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await context.Response.WriteAsync("unauthorized");
                return;
            }

            await next(context);
        });
    }

    /// <summary>The browser dashboard's session cookie, set by <c>POST /login</c> after a token check.</summary>
    private const string AuthCookieName = "cifail_auth";

    private static bool WantsHtml(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorized(HttpRequest request, IReadOnlyList<NamedToken> tokens)
    {
        // API clients present a bearer token; the browser dashboard presents the auth cookie that
        // POST /login set after validating that same token. Either authorizes the request.
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && MatchesAnyToken(header[prefix.Length..].Trim(), tokens))
            return true;

        return request.Cookies.TryGetValue(AuthCookieName, out var cookie)
            && MatchesAnyToken(cookie ?? string.Empty, tokens);
    }

    /// <summary>Constant-time compare a presented secret against every configured token.</summary>
    private static bool MatchesAnyToken(string provided, IReadOnlyList<NamedToken> tokens)
    {
        var bytes = Encoding.UTF8.GetBytes(provided);

        // Check every token without early-exit so a match never leaks (timing) which one it was.
        var authorized = false;
        foreach (var t in tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(bytes, Encoding.UTF8.GetBytes(t.Token)))
                authorized = true;
        }
        return authorized;
    }

    /// <summary>
    /// Public paths served without a token: the liveness probe and the sign-in flow (R28). Unlike
    /// the old R12 shell, the dashboard itself now renders data server-side, so it requires the
    /// auth cookie that signing in issues — only the sign-in page and its POST target stay open
    /// (the POST validates the submitted token itself; a signing-in browser has no cookie yet).
    /// </summary>
    private static readonly HashSet<string> PublicPaths =
        new(StringComparer.OrdinalIgnoreCase) { "/healthz", "/login", "/ui/login" };

    private static void MapEndpoints(
        WebApplication app,
        IReadOnlyList<NamedToken> tokens,
        IAiEmbedder? embedder,
        NotificationDispatcher? notifications)
    {
        // Liveness/readiness — unauthenticated, used by the Helm probes.
        app.MapGet("/healthz", () => Results.Text("ok"));

        // Sign-in (R28). Validates the entered token against the configured set and, on success,
        // sets an HttpOnly cookie the auth middleware accepts. When the server runs open (no
        // tokens), there's nothing to check — just bounce back to the dashboard. The POST lives
        // under /ui/ (not /login) so it never collides with the /login Razor page's route.
        app.MapPost("/ui/login", async (HttpRequest request) =>
        {
            // ReadFormAsync throws on a non-form body, which would surface as a bare 500 — and this
            // route is public, so anyone can reach it. Answer a malformed post honestly instead.
            if (!request.HasFormContentType)
                return Results.BadRequest("expected a form post");

            var form = await request.ReadFormAsync();
            var token = form["token"].ToString();

            if (tokens.Count == 0 || MatchesAnyToken(token, tokens))
            {
                if (tokens.Count > 0)
                    request.HttpContext.Response.Cookies.Append(AuthCookieName, token, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Secure = request.IsHttps,
                        Path = "/",
                    });
                return Results.Redirect("/");
            }

            return Results.Redirect("/login?error=1");
        }).DisableAntiforgery(); // a plain HTML form post, not a Blazor form — opt out of token validation

        // Dashboard resolve action (R28): a plain form post from the detail pane. Mirrors the JSON
        // /resolve handler (manual resolution + a Resolved notification), then returns to the page.
        app.MapPost("/ui/resolve", async (HttpRequest request, Func<IAnalysisStore> stores) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest("expected a form post");

            var form = await request.ReadFormAsync();
            if (!long.TryParse(form["id"], out var id) || string.IsNullOrWhiteSpace(form["note"]))
                return Results.BadRequest("id and note are required");

            using var store = stores();
            if (store.SetResolution(id, form["note"].ToString()) && store.GetById(id) is { } updated)
                notifications?.Dispatch(new Notification(NotificationEvent.Resolved, updated));

            // Only ever redirect to a local path (the hidden field is page-supplied, but guard anyway).
            var returnTo = form["return"].ToString();
            return Results.Redirect(returnTo.StartsWith('/') ? returnTo : "/");
        }).DisableAntiforgery(); // plain HTML form post from the detail pane

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

        // Prometheus scrape target (R31). Same StatsService the /stats endpoint uses, rendered
        // as text exposition — so the dashboard, `cifail stats` and your Grafana board can
        // never disagree about a number.
        //
        // Authenticated like everything else. Prometheus supports a bearer token in the scrape
        // config, and the alternative (a public /metrics) would leak rule ids, ecosystems and
        // failure counts to anyone who can reach the pod.
        app.MapGet("/metrics", (Func<IAnalysisStore> stores) =>
        {
            using var store = stores();
            var stats = StatsService.Compute(store, new StatsQuery { Top = MetricsTopFailures });
            return Results.Text(PrometheusOutput.Build(stats, MetricsTopFailures),
                PrometheusOutput.ContentType);
        });

        // A static description of this API (R31). Embedded, not generated: the routes are
        // hand-written minimal-API lambdas with no metadata to reflect over, and a generator
        // would pull Swashbuckle into an assembly that deliberately has no NuGet dependencies.
        app.MapGet("/openapi.json", () => Json(OpenApiDocument.Value));

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

    /// <summary>
    /// How many per-fingerprint series <c>/metrics</c> exposes. Small on purpose: a
    /// fingerprint label is unbounded, and cardinality is what kills a Prometheus server.
    /// </summary>
    private const int MetricsTopFailures = 10;

    /// <summary>
    /// The embedded OpenAPI description, read once. A missing resource would mean a broken
    /// build rather than a runtime condition worth handling gracefully, so it throws.
    /// </summary>
    private static readonly Lazy<string> OpenApiDocument = new(() =>
    {
        var assembly = typeof(CiFailServer).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("openapi.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static bool ParseBool(string? v) =>
        v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private sealed record ResolveRequest(string Note);
}
