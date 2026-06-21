using System.Text.Json;
using CiFail.Core.Analysis;
using CiFail.Core.Output;
using CiFail.Core.Rules;
using CiFail.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
        builder.WebHost.UseUrls(options.ResolvedUrl);

        // Rule packs are immutable for the process lifetime — load them once.
        builder.Services.AddSingleton(new RuleEngine(RulePackLoader.LoadAll()));

        // A fresh store is opened per request: EF DbContexts and the SQLite repo are not
        // thread-safe, and the server is otherwise stateless. The git reconciler is never
        // wired here — a central server has no working tree (see R11 for the client-driven path).
        Func<IAnalysisStore> storeFactory = () => StoreFactory.Create(options.Database);
        builder.Services.AddSingleton(storeFactory);

        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
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
            var service = new AnalysisService(engine, store);
            var analysis = service.Analyze(source, body, options);
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

        // A single record, or 404.
        app.MapGet("/history/{id:long}", (long id, Func<IAnalysisStore> stores) =>
        {
            using var store = stores();
            var record = store.GetById(id);
            return record is null
                ? Results.NotFound()
                : Json(JsonSerializer.Serialize(StoredAnalysisJson.ToDto(record), AnalysisJson.Options));
        });

        // Manual resolution. Body: { "note": "..." }. Returns the updated record, or 404.
        app.MapPost("/resolve/{id:long}", async (long id, HttpRequest request, Func<IAnalysisStore> stores) =>
        {
            ResolveRequest? req;
            try { req = await request.ReadFromJsonAsync<ResolveRequest>(); }
            catch (JsonException) { return Results.BadRequest("invalid JSON body"); }

            if (req is null || string.IsNullOrWhiteSpace(req.Note))
                return Results.BadRequest("note is required");

            using var store = stores();
            if (!store.SetResolution(id, req.Note))
                return Results.NotFound();
            var updated = store.GetById(id)!;
            return Json(JsonSerializer.Serialize(StoredAnalysisJson.ToDto(updated), AnalysisJson.Options));
        });
    }

    private static IResult Json(string json) => Results.Text(json, "application/json");

    private static bool ParseBool(string? v) =>
        v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private sealed record ResolveRequest(string Note);
}
