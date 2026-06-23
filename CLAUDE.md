# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`cifail` — a local-first CLI that analyzes CI/build/test logs and reports what broke,
whether it has happened before, and how to fix it. Rules-first and fully offline;
optional local AI (Ollama) is a deferred add-on, never required. .NET 8, C#.

## Commands

```bash
dotnet build                                  # build the solution
dotnet test                                   # run all tests
dotnet test --filter FullyQualifiedName~RuleEngineTests   # one test class
dotnet test --filter "DisplayName~nu1101"                 # one test by name fragment

# Run the CLI from source:
dotnet run --project src/CiFail.Cli -- analyze samples/nuget-nu1101.log
dotnet run --project src/CiFail.Cli -- analyze --json build.log
cat build.log | dotnet run --project src/CiFail.Cli -- analyze   # stdin
dotnet run --project src/CiFail.Cli -- history
dotnet run --project src/CiFail.Cli -- rules list
dotnet run --project src/CiFail.Cli -- reconcile   # auto-resolve fixed failures (git)
dotnet run --project src/CiFail.Cli -- init        # install git hooks for auto-reconcile

# Package as a global tool:
dotnet pack src/CiFail.Cli/CiFail.Cli.csproj -c Release -o ./nupkg

# Build self-contained, single-file native binaries (no .NET needed to run them):
bash scripts/publish.sh                # all 6 RIDs -> dist/ (SLIM: SQLite only)
bash scripts/publish.sh linux-x64      # one RID
dotnet publish src/CiFail.Cli/CiFail.Cli.csproj -c Release -r win-x64 -o dist/win-x64

# Full build WITH external DB providers (Postgres/MySQL/SQL Server/MongoDB) — for Docker:
dotnet build src/CiFail.Cli/CiFail.Cli.csproj -p:IncludeExternalDb=true

# Docker image (FULL build: all DB providers + git). Built/pushed to GHCR by release.yml;
# build locally with:
docker build -t cifail .
docker run --rm -v "$PWD:/work" cifail analyze build.log
# CI integration (R5): composite GitHub Action `action.yml` (wraps the GHCR image, writes
# the analysis to the step summary) and GitLab template `ci-templates/gitlab.yml`. The
# image symlinks /app/cifail -> /usr/local/bin/cifail so `cifail` is on PATH when a runner
# overrides the entrypoint (GitLab).

# Run the external-DB contract tests against real engines (needs Docker):
CIFAIL_DB_IT=1 dotnet test tests/CiFail.Providers.Tests
docker compose -f docker-compose.test.yml up -d   # manual DBs for --db-* runs
# Releases are cut by pushing a tag (git tag v0.1.0 && git push origin v0.1.0),
# which triggers .github/workflows/release.yml to build the matrix + attach binaries.
```

The published executable is named **`cifail`** (set via `<AssemblyName>`), not
`CiFail.Cli`. Self-contained/single-file props in `CiFail.Cli.csproj` are gated on
`'$(RuntimeIdentifier)' != ''`, so they only apply during `dotnet publish -r <rid>` and
never affect plain build/test/pack.

`cifail serve` (R7) is a real HTTP API in `src/CiFail.Server` (ASP.NET Core minimal API),
**size-gated into the full / Docker build only** via the `CIFAIL_SERVER` symbol (tied to
`-p:IncludeExternalDb=true`, exactly like `CIFAIL_EXTERNAL_DB`). The slim binaries get
neither the `serve` command nor ASP.NET Core. The Docker runtime base is therefore
`dotnet/aspnet:8.0` (not `runtime:8.0`). The Helm chart in `deploy/helm/cifail` runs it; see
`deploy/README.md`. Auth (R9): every route except `/healthz` requires `Authorization: Bearer
<token>` when a token is set via `CIFAIL_SERVER_TOKEN` / `serve --token` (constant-time compare via
`CryptographicOperations.FixedTimeEquals`); started without one, serve runs open and logs a loud
warning. Clients send it with `--server-token` / `CIFAIL_SERVER_TOKEN` (see
`HttpAnalysisStore.TokenEnvVar`, the single source of the env-var name). **R20 hardening:**
multiple per-client tokens (`CIFAIL_SERVER_TOKENS` comma list + `serve --tokens-file`, parsed by
`ServerTokens` into `NamedToken`s; `ServeOptions.ResolvedTokens()` merges them with the single
token, and `IsAuthorized` constant-time-compares against all without early-exit) so a client can be
revoked individually; opt-in **mTLS** (`serve --client-ca <pem> --tls-cert <pfx>` → `ConfigureMutualTls`
drives Kestrel with `ClientCertificateMode.RequireCertificate` + a `CustomRootTrust` chain check);
and an AI cost guardrail (`Core/Ai/RateLimitedAiAnalyzer`, a decorator `AiFactory.Create` wraps when
`AiConfig.Limits` is set — caps calls/run, calls/minute, and prompt chars; off = unlimited). Build it locally with
`dotnet build src/CiFail.Cli/CiFail.Cli.csproj -p:IncludeExternalDb=true`
then `dotnet run --project src/CiFail.Cli -- serve --port 8080` (or hit the in-process host
in `tests/CiFail.Server.Tests`, which needs no Docker).

**Always set `CIFAIL_HOME` to a temp dir when manually running the CLI.** The tool
writes history to `~/.cifail/history.db`, and .NET's `SpecialFolder.UserProfile`
ignores a shell-exported `USERPROFILE`, so without `CIFAIL_HOME` your manual runs
pollute the real home dir:

```bash
export CIFAIL_HOME="$(mktemp -d)/d"
dotnet run --project src/CiFail.Cli -- analyze samples/nuget-nu1101.log
```

## Architecture

Two-layer design so the core logic stays reusable by a future GUI/web UI:

- **`src/CiFail.Core`** — all logic, no console dependencies.
- **`src/CiFail.Cli`** — Spectre.Console.Cli `CommandApp`; thin commands that call Core.
- **`src/CiFail.Providers`** — external DB providers (EF Core + MongoDB), size-gated out
  of the default binary (see "External database providers" below).
- **`src/CiFail.Server`** — `cifail serve` HTTP API (ASP.NET Core), size-gated into the
  full build only (`CIFAIL_SERVER`); a thin host over `AnalysisService` + `IAnalysisStore`.
  The shared JSON contract lives in `Core/Output/AnalysisJson.cs` + `StoredAnalysisJson.cs`
  so the CLI `--json` and the server serialize one identical schema (`AnalysisJson.FromDto`
  reconstructs the domain `Analysis` from the DTO; `ToDto∘FromDto` round-trips byte-for-byte).
  `Core/Storage/HttpAnalysisStore.cs` is the matching read/resolve client (`http` store provider;
  `--server <url>`); **R18** adds `Core/Storage/HttpAnalyzeClient.cs` which POSTs to the server's
  `/analyze` so `cifail analyze --server` runs the full pipeline server-side (the CLI builds units
  client-side, supporting `--format`, and renders the reconstructed result identically to a local run).
  R12 adds a bundled web dashboard: `src/CiFail.Server/wwwroot/index.html` is an
  **EmbeddedResource** (one zero-build page, no node stage), read once and served at `/` +
  `/index.html`; those paths plus `/healthz` are in `PublicPaths` (exempt from the R9 token —
  the shell collects the token in-page and sends it on its own API calls). JSON is **PascalCase**
  (`AnalysisJson.Options`), so the dashboard JS keys off `r.Id`/`r.Status`/etc.
- **`tests/CiFail.Core.Tests`** — xUnit + FluentAssertions; fixtures in `fixtures/*.log`.
- **`tests/CiFail.Providers.Tests`** — shared store contract + Docker-gated DB tests.
- **`tests/CiFail.Server.Tests`** — boots a real serve instance on a random port (no Docker).

### The analyze pipeline (`Core/Analysis/AnalysisService.cs`)

`Analyze()` is the orchestrator and the single entry point for the whole flow:

1. `Ingest/LogNormalizer.Build` — strip ANSI, normalize newlines, drop leading CI
   timestamps. Also exposes `Scrub()`, which collapses volatile tokens (paths,
   numbers, GUIDs) — used for fingerprints and similarity so cosmetic differences
   don't matter.
2. `Ingest/EcosystemDetector.Detect` — marker-count heuristic (dotnet/node/python/java/go/rust/ruby/php/cpp/infra/swift/android),
   falls back to `generic`; overridable via `--type`.
3. `Rules/RuleEngine.Match` — runs applicable rules, returns matches ranked by
   confidence. Rules whose `ecosystem` is `generic` always apply; ecosystem-specific
   rules only apply when detected (or when ecosystem is undetected). Named regex
   capture groups are interpolated into the rule's `fix` template via `{name}`.
4. `Analysis/FingerprintBuilder` — `ruleId:hash` identity from scrubbed signature.
5. Similarity + persistence (only when an `IAnalysisStore` is supplied): TF-IDF cosine
   over scrubbed bag-of-terms vs. stored history; then persists the run unless
   `--no-history`. **R10 (opt-in):** when an `IAiEmbedder` is wired *and* the store also
   implements `Similarity/ISimilaritySearch` (only the `pgvector` provider does), similarity is
   served from the DB over a stored embedding instead of loading the corpus; the embedding is
   also saved on the record. No embedder (the default) or a non-vector store → unchanged TF-IDF.
   The embedder is built only when `ai.embeddings` is opted in (`CIFAIL_AI_EMBEDDINGS`); it's
   best-effort (offline model → null → TF-IDF fallback), like the AI suggestion path.

`AnalysisService.CreateDefault()` = rules only, no store (used by tests and the
offline path). `CreateWithStore(store, git?)` = adds similarity + history (and, when an
`IGitRepo` is passed, git correlation). The store is injected via the `IAnalysisStore`
interface so the pipeline never hard-depends on SQLite and tests can use a temp-file
repository.

### Git-correlated auto-resolutions (R3, `Core/Git/` + `Core/Analysis/ResolutionReconciler.cs`)

Each persisted record carries git context (`repo_id` = root commit, `git_commit`,
`git_branch`, `git_dirty`) plus a lifecycle: `status` (open/resolved), `resolution_source`
(manual/auto), `resolved_commit`. See `AnalysisStatus`/`ResolutionSource` constants in
`StoredAnalysis.cs`. Two store methods drive it: `GetOpenFailures(repoId)` and
`SetAutoResolution(id, commit, note)` — implemented across all providers, **and (R11) over
HTTP** by `HttpAnalysisStore` (`GET /repos/{repoId}/open`, `POST /resolve/{id}?source=auto&
commit=<sha>`), so the same reconciler works against a remote `--server`.

`GitContext.Detect(dir)` shells out to the system `git` (no native lib) and returns null
when git is missing / not a repo / no commits — so everything degrades to "just SQLite,
no correlation". It's behind `IGitRepo` so `ResolutionReconciler` is unit-tested with a
fake (`GitContext` itself is tested against a temp real repo).

`ResolutionReconciler.Reconcile(store, repo, observedFingerprints)`: a failure recorded at
commit A is auto-resolved when HEAD (B) is a descendant of A **and** its fingerprint isn't
in `observedFingerprints` (the failures seen in the current run) — crediting the commits in
`(A, B]`. `SetAutoResolution` only touches still-open rows, so **manual resolutions always
win**. The CLI runs this after `analyze` (passing the just-seen fingerprints) and via the
standalone `reconcile` command (empty observed set); `init` installs post-commit/post-merge
hooks that call `cifail reconcile`. Skip it all with `analyze --no-git`. `reconcile --server
<url>` (R11) runs the same flow against a remote server — the git context is always detected on
the client (the server has no working tree); only the store differs.

### Structured test-report ingestion (R17, `Core/Ingest/Reports/TestReportParser.cs`)

`analyze --format auto|log|junit|trx` (default `auto`: sniff by extension/root element). JUnit XML
and .NET TRX are parsed (BCL `System.Xml.Linq`, namespaces ignored by local-name match → ships slim)
into `TestFailure`s; the CLI expands each failing test into its own **analysis unit**
(`source = <path>::<FullName>`, text = `TestFailure.ToLogText()`) so rules/similarity/fingerprints
run per test. A clean report with no failures exits 0. `--annotations` emits GitHub
`::error title=…::…` lines per failing test (only under `GITHUB_ACTIONS=true`; escaped per the
workflow-command rules). Unit-building + annotation logic live in `AnalyzeCommand`; the parser is in Core.

### Insights / stats (R16, `Core/Analysis/StatsComputer.cs` + `Storage/IAnalysisStats`)

`cifail stats` turns history into signal: open/resolved/unmatched counts, by-ecosystem
breakdown, top recurring fingerprints, recurrence rate, mean-time-to-resolution, and a
**flaky** flag (a fingerprint that recurred *after* it had been resolved). `StatsComputer.Compute`
is a **pure** function over `StoredAnalysis` rows — the single source of truth, so every backend
returns identical numbers. `IAnalysisStats` is a **side-interface** (like `IFingerprintCounter`):
SQLite/EF/Mongo implement it by pushing the cheap filters (repo, and for SQLite `since`) into the
query then delegating to `StatsComputer`; `HttpAnalysisStore` implements it by calling the server's
`GET /stats`. `StatsService.Compute(store, query)` routes to the interface when present, else falls
back to `GetRecent(scanLimit)` + `StatsComputer`. JSON contract: `Output/StatsJson.cs` (PascalCase,
durations as seconds). The server exposes `GET /stats?since=&repo=&top=` and the bundled dashboard
renders a trends strip from it. Add new aggregations in `StatsComputer` (+ `StatsSnapshot`/`StatsJson`),
not per-store.

**Per-test flakiness (R26):** `cifail stats --tests` switches to a per-test view. `TestFlakeComputer.Compute`
is a **pure** function (mirrors `StatsComputer`) over the rows R17 persists per failing test
(`source = "<path>::<FullName>"`): it keeps rows whose source contains `::`, parses the `FullName`
(split on the **first** `::` — the path has none, a C++ test name might, matching `SarifOutput`),
groups by it, and reports failure count, distinct-day spread, last-seen, open count, and the same
recurred-after-resolved **flaky** flag. `TestStatsService.Compute(store, query)` just scans
`GetRecent(scanLimit)` + the computer — uniform across every store (incl. `--server`, whose http
store implements `GetRecent`), so no new store interface. JSON: `Output/TestStatsJson.cs`. **Honest
limitation, stated in the output:** cifail records failures, not passes — so this is recurring/flaky,
not a true fails/total pass-rate.

### Failure clustering (R25, `Core/Analysis/FailureClusterer.cs` + `Storage/IClusterer`)

`cifail clusters` groups near-duplicate failures so you see distinct root causes, not a flat list.
`FailureClusterer.Compute(corpus, ClusterQuery)` is a **pure** function (mirrors `StatsComputer`):
single-link agglomeration via union-find over **TF-IDF cosine** on the already-scrubbed
`CorpusEntry.Terms`. It blocks comparisons with an inverted index (`BuildBlocks` — only failures
sharing a term with `2 ≤ df ≤ ⌈0.8·n⌉` are compared; ubiquitous/unique terms carry no pairing
signal) to stay near-linear up to `ScanLimit` (2000). New TF-IDF helpers `Idf`/`WeightVector`/
`CosineVectors` give it one corpus-wide IDF (the pairwise `Cosine` uses a 2-doc IDF, wrong for
batch clustering). `ClusterQuery` = `{Threshold=0.5, Top=10 (0=all), Since?, RepoId?,
IncludeSingletons, ScanLimit}`; a `FailureCluster` = `{Label (dominant matched RuleId, else
"unmatched"), Count, MemberIds (newest first), Ecosystems, LastSeen}`. `IClusterer` is a
**side-interface** (like `IAnalysisStats`): `HttpAnalysisStore` implements it via `GET /clusters`;
SQLite/EF/Mongo don't, so `ClusterService.Compute(store, query)` falls back to `LoadCorpus(ScanLimit)`
(R21-cached) + `FailureClusterer`. JSON: `Output/ClustersJson.cs` (PascalCase). Server exposes
`GET /clusters?threshold=&since=&repo=&top=&all=`; the dashboard renders a clusters card.

### Rule authoring tooling (R15, `Core/Rules/RulePackValidator.cs` + `Cli/Commands/Rules*.cs`)

`cifail rules` has `list`, plus `test <regex>` (try a regex against a log, show captures),
`validate [path]` (lint packs — malformed regex, missing id/match, bad confidence, duplicate ids;
non-zero exit on error, run in CI on the shipped packs), and `explain <id>`. `RulePackValidator`
surfaces the diagnostics the normal load path silently skips, using `RulePackLoader.DeserializeRaw`
(unfiltered) + `EmbeddedDocuments()`. Duplicate id within a tier = error; embedded↔user = override
warning. See `CONTRIBUTING.md` for the add-a-rule workflow.

**AI-assisted authoring (R23, `cifail suggest-rule`):** for a log no rule matches, an AI drafts a
rule and **cifail's validators gate it**. `Ai/IAiRuleDrafter` is a side-interface (like `IAiEmbedder`,
not on `IAiAnalyzer`) implemented by `OllamaAnalyzer`; `AiPrompt.BuildRuleDraft` + `AiRuleDraftParser`
build/parse the draft; `AiFactory.CreateDrafter` returns the raw provider (not the rate-limit wrapper —
drafting is one interactive call). `Rules/RuleDraftValidator.Validate(draft, log)` is the gate: reuses
`RulePackValidator` for the standard lint, then enforces the draft-specific rules — regex compiles
within a **1s timeout** (ReDoS guard), **actually matches the log** (anti-hallucination), isn't
overbroad (`.*`/`.+`/…), id is kebab-case, and forces a conservative `DraftConfidence` (the model never
asserts its own). `SuggestRuleCommand` previews the YAML and, with `--write`, deserialize-appends it to
`~/.cifail/rules/suggested.yaml` (`CiFailPaths.SuggestedRulesPath`, serialized via `RulePackLoader.Serialize`).
Offline-degrades (no model → friendly message, exit 1).

### Rule packs (`Core/rulepacks/*.yaml`)

Rules are **data, not code** — this is the primary extension point. Each pack is a
YAML list of `{ id, ecosystem, category, title, match (regex), confidence, fix, docs }`.
Packs are **embedded resources** (see `EmbeddedResource` glob in `CiFail.Core.csproj`),
loaded by `RulePackLoader` from the assembly; users can add `~/.cifail/rules/*.yaml`,
and a user rule with a duplicate `id` overrides the embedded one. A malformed regex is
skipped, never fatal. To add coverage for a new failure, add a rule + a fixture +
a test case in `RulePackBreadthTests` — usually no C# changes needed.

### Storage (`Core/Storage/`)

Persistence is pluggable behind `IAnalysisStore` (which extends `IDisposable`). Backends
are chosen at runtime by name through a small registry:

- `IStoreProvider` = a named factory (`Name`, `Description`, `Create(connectionString)`).
- `StoreRegistry` = static map of provider name → provider. **SQLite is registered in the
  static ctor** (always available, no extra deps). External providers register themselves
  via `ExternalProviders.RegisterAll()` — see below.
- `StoreFactory.Create(...)` resolves a `DatabaseConfig` (or CLI/env/file via
  `ConfigLoader`) to a store; an unknown/unbundled provider throws
  `StoreProviderNotAvailableException` (friendly "use the Docker/full build" message).
- Config precedence (`Configuration/ConfigLoader`): CLI flags (`--db-provider`,
  `--db-connection`) > env (`CIFAIL_DB_PROVIDER`, `CIFAIL_DB_CONNECTION`) >
  `~/.cifail/config.yaml` (`database: { provider, connectionString }`) > default (sqlite).
  Provider name is normalized to lowercase. CLI commands open the store via
  `StoreSupport.TryCreate(settings)`, which prints the error and returns null (→ exit 2).

`SqliteAnalysisRepository` (Microsoft.Data.Sqlite) is the default: creates its schema on
first use, term vectors stored as JSON per row and reloaded as the similarity corpus, and
`Pooling=False` so the db file handle releases promptly on dispose (otherwise the file
stays locked, which breaks temp-file test cleanup). All paths resolve through
`CiFailPaths`, which honors `CIFAIL_HOME`. **R21:** `LoadCorpus` caches its result keyed on a
cheap `(row-count, max-id, limit)` signature so analyzing a structured report (which expands into
many units that each ask for the same corpus) doesn't reload up to 2000 rows per unit; inserts move
the signature and in-place updates (`Save`/`SetResolution`/`SetAutoResolution`) clear the cache, so
it stays consistent with the DB.

### External database providers (`src/CiFail.Providers`, size-gated)

PostgreSQL/MySQL/SQL Server (EF Core — `AnalysisEntity` mapped by an abstract `AnalysisDbContext`,
shared `EfAnalysisStore`, schema via `EnsureCreated()`, **no migrations**) and MongoDB
(`MongoAnalysisStore`, document-per-analysis, sequential ids minted from a `counters` doc)
live in a **separate assembly** so the default native binary stays SQLite-only and small.

- **pgvector (R10):** a `pgvector` provider for vector similarity. `AnalysisDbContext` is now
  abstract with a virtual `MapEmbedding` hook: `CiFailDbContext` (Postgres/MySQL/SQL Server)
  **ignores** the `AnalysisEntity.Embedding` (`Pgvector.Vector?`) property; `PgVectorDbContext`
  maps it to a `vector(N)` column + an HNSW cosine index and declares the `vector` extension.
  `PgVectorAnalysisStore : EfAnalysisStore, ISimilaritySearch` adds `FindSimilar` (cosine via
  `Pgvector.EntityFrameworkCore`, `UseNpgsql(cs, o => o.UseVector())`). `EfAnalysisStore` is no
  longer sealed (`protected Db`/`Map`) and always sets `Embedding` — ignored unless the column
  exists. `N` comes from `CIFAIL_AI_EMBED_DIM` (default `ConfigLoader.DefaultEmbeddingDimensions`
  = 768) and **must match the embedder's output**. Packages `Pgvector` + `Pgvector.EntityFrameworkCore`.
  **R21:** the index method is selectable via `CIFAIL_AI_VECTOR_INDEX` (`hnsw` default | `ivfflat`),
  normalized by `PgVectorDbContext.NormalizeIndexMethod` and threaded through `PgVectorStoreProvider`.
  `ivfflat` learns centroids from existing rows, so an index built on the empty table that
  `EnsureCreated()` produces has poor recall until rebuilt — `hnsw` is the safe default. Covered by `PgVectorIntegrationTests`
  (Docker-gated) + a non-gated `NormalizeIndexMethod` theory.

- Inclusion is opt-in: the CLI references `CiFail.Providers` and defines the
  `CIFAIL_EXTERNAL_DB` compile symbol **only when built with `-p:IncludeExternalDb=true`**
  (the Docker / full build). `Program.cs` then calls `ExternalProviders.RegisterAll()`
  inside `#if CIFAIL_EXTERNAL_DB`. `scripts/publish.sh` builds SLIM (no flag).
- EF entities store timestamps as ISO-8601 (`"O"`) **strings** (not `DateTimeOffset`
  columns) to keep every relational engine's schema identical and dodge provider date
  quirks. Column/table/index names mirror the SQLite schema.
- Tests: `tests/CiFail.Providers.Tests`. One shared `StoreContract.Verify(store)` is the
  behavioural contract for every backend. It runs locally against `EfAnalysisStore` over
  **EF Core SQLite in-memory** (no Docker). Real-engine tests (`RealEngineContractTests`,
  Testcontainers) are `[SkippableFact]` gated on **`CIFAIL_DB_IT=1`** — skipped locally,
  run by the `db-integration` CI job. `PgVectorIntegrationTests` is in the same assembly/gate
  (uses the `pgvector/pgvector` image). `docker-compose.test.yml` spins up all engines (incl.
  a `pgvector` service on port 5433) for manual `--db-*` runs.

### Notifications / webhooks (R13, `Core/Notifications/`)

Outbound alerts fired **only server-side** (the CLI stays quiet/offline). `INotifier` is one
channel (`Name`, `void Notify(Notification)`); a `Notification` is a `(NotificationEvent, StoredAnalysis)`
where the event is `NewFailure` / `Recurrence` / `Resolved` (kebab-case `EventKey` used in config
and payloads). `NotificationDispatcher` fans a notification to every channel: it skips disabled
events (empty filter = all), dedupes per `EventKey|Fingerprint` within a window (default 5 min;
`TimeSpan.Zero` disables), and isolates each channel in try/catch so a broken one never affects
analysis. `NotificationDispatcher.FromConfig(NotificationsConfig)` builds it (or returns **null**
when no channel is set) — no provider registry, just a direct builder. Channels live in
`Notifications/Channels/`: `SlackNotifier`, `WebhookNotifier` (both POST JSON via the shared
`NotifierHttp.PostJson`, 10s timeout), and `SmtpNotifier` (`System.Net.Mail`, password from an env
var). **R27** adds `DiscordNotifier` (`{content, embeds[]}`) and `TeamsNotifier` (legacy MessageCard,
themed per event) — both take an injectable poster (`internal` ctor, exposed to tests via
`InternalsVisibleTo`) defaulting to `NotifierHttp.PostJson` so payload shape is unit-tested without a
network call — plus `GitHubIssueNotifier`, which opens one issue per distinct failure and **comments
instead of duplicating** on recurrence, idempotent via a hidden `<!-- cifail:<fingerprint> -->`
marker (the R19 MR/PR-comment scheme). Its REST calls sit behind `IGitHubIssueClient`
(`FindOpenIssue`/`CreateIssue`/`CommentOnIssue`); prod impl `HttpGitHubIssueClient` (raw HttpClient,
token from an env var, scans the cifail-labelled open issues for the marker), a fake drives the tests.
Config: `NotificationsConfig` on `CiFailConfig` (`events`, `slackWebhookUrl`, `webhookUrl`,
`discordWebhookUrl`, `teamsWebhookUrl`, `dedupeSeconds`, `smtp`, `gitHub {repo, tokenEnv, labels,
apiBaseUrl}`); `ConfigLoader` overrides the webhook URLs from `CIFAIL_NOTIFY_SLACK_URL` /
`CIFAIL_NOTIFY_WEBHOOK_URL` / `CIFAIL_NOTIFY_DISCORD_URL` / `CIFAIL_NOTIFY_TEAMS_URL` (secrets stay out
of the file). Wiring: `ServeCommand` builds the dispatcher and passes it via `ServeOptions.Notifications`;
`CiFailServer` dispatches on `/analyze` (new vs. recurrence decided by `IFingerprintCounter.CountByFingerprint`
— a **side-interface** implemented by all stores, like `ISimilaritySearch`, to avoid changing the
`IAnalysisStore` contract) and on `/resolve` (`Resolved`).

### Output (`Cli/Output/`)

`ConsoleRenderer` (Spectre panels/tables) and `JsonOutput` are separate. `JsonOutput`
serializes an explicit DTO, **not** the domain model, so the `--json` contract can
evolve independently — keep it stable. Exit codes: `0` matched, `1` analyzed but no
rule matched, `2` input error.

The human-facing wording is deliberately **plain / beginner-oriented** (e.g. "What
broke", "How to fix it", confidence shown as high/medium/low not `0.90`, and a
copy-paste `cifail resolve <id>` tip). Keep that tone when changing output. The
`resolve` tip needs the saved history id, which the pipeline surfaces via
`Analysis.HistoryId` (set by `AnalysisService` from the store's `Save`, null when not
persisted).

**Report output (R24, `Core/Output/`):** `analyze --report sarif|markdown` (+ optional
`--report-out <file>`) renders the results into a CI-native format. `SarifOutput.Build` and
`MarkdownOutput.Build` both take `IReadOnlyList<AnalysisJson.AnalysisDto>` (the stable `--json`
DTO — no domain coupling), with confidence→label shared via `ReportFormatting` so the two never
disagree. SARIF is 2.1.0: one `run`, `tool.driver.rules[]` deduped by `RuleId`, one `results[]`
per unit (`ruleIndex` resolves, `level` from confidence buckets, `partialFingerprints
["cifailFingerprint/v1"]` = the run fingerprint so Code Scanning tracks a failure across runs);
unmatched units → a synthetic `cifail-unmatched` note. A report-expanded test source
(`file::TestName`) contributes the **file** part as the artifact uri (relativized to cwd; stdin →
`"stdin"`). The renderers live in **Core** (not `Cli/Output/`) since the server may reuse them.
`AnalyzeCommand.EmitResults` wires it for both the local and `--server` paths: with `--report-out`
the file is written **and** the normal console/`--json` view still shows; without it the report
takes over stdout and suppresses the normal view. The GitHub Action exposes it via a `sarif:`
input (→ `--report sarif --report-out`), paired with `github/codeql-action/upload-sarif`.

## Conventions / gotchas

- Spectre.Console.Cli 0.55: `Command<T>.Execute` and `.Validate` overrides are
  `protected` and `Execute` takes a `CancellationToken`.
- `FluentAssertions` is pinned to **7.2.0** deliberately — v8+ is commercially
  licensed and incompatible with this MIT project. Do not upgrade it.
- Targets `net8.0` even though only the .NET 9 SDK is installed locally (builds fine).
- `*.log` is globally gitignored; sample and fixture logs are committed via explicit
  `!` un-ignore rules in `.gitignore` — keep new committed logs under `samples/` or a
  `fixtures/` dir.
- The `Analysis` type lives in namespace `CiFail.Core.Models` but there's also a
  `CiFail.Core.Analysis` namespace — inside the latter, refer to the type as
  `Models.Analysis`.
