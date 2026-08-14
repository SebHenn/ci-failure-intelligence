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
dotnet run --project src/CiFail.Cli -- config      # resolved paths/settings + lint config.yaml

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
# the analysis to the step summary). For GitLab there are TWO files and they are not
# interchangeable: `templates/cifail.yml` is the CI/CD **component** (spec:inputs — stage,
# log, image, args, fail, comment, comment_image) that the README leads with, and
# `ci-templates/gitlab.yml` is the older hidden-job (`.cifail` + `extends:`) template kept
# for back-compat with runners that can't use components. Change both or neither. The image
# symlinks /app/cifail -> /usr/local/bin/cifail so `cifail` is on PATH when a runner
# overrides the entrypoint (GitLab).

# Run the external-DB contract tests against real engines (needs Docker):
CIFAIL_DB_IT=1 dotnet test tests/CiFail.Providers.Tests
docker compose -f docker-compose.test.yml up -d   # manual DBs for --db-* runs
# Releases are cut by pushing a tag (git tag v0.2.0 && git push origin v0.2.0). The tag must
# equal <Version> in Directory.Build.props — release.yml's `verify` job enforces it, because a
# mismatch is what made every 0.1.0 install path 404. One Linux runner cross-publishes all 6
# RIDs (there is no build matrix; only `smoke` is a matrix, ubuntu + macos).
```

**Release chain (`release.yml`): `verify → build → release(draft) → smoke → finalize`,** with
`nuget` and `docker` hanging off `verify`. The GitHub Release stays a **draft** until the real
`install.sh` has installed the real assets on Linux *and* macOS; `finalize` un-drafts it and
force-moves the `v1` tag that `uses: SebHenn/ci-failure-intelligence@v1` resolves to.

**`main` is a protected branch.** PRs require the three `ci.yml` checks (`build-test`,
`docker-smoke`, `db-integration`) and must be **up to date with `main`** before merging; force
pushes and deletion are blocked. **Admins are exempt** (`enforce_admins: false`), so a direct
`git push origin main` by the owner still works — protection is a floor for contributors and
dependabot, not a workflow change for the maintainer. It does **not** constrain the release
chain: `release.yml`'s only force-push targets the `v1` *tag*, and tags aren't branch-protected.
Don't add a workflow that commits to `main` without revisiting this.

**NuGet publishing uses trusted publishing (OIDC) — there is no `NUGET_API_KEY` secret.** The
`nuget` job declares `id-token: write`, and `NuGet/login@v1` exchanges a GitHub-signed token for
an API key valid one hour. nuget.org matches the token's claims against a policy naming the repo
owner, the repo, and **the workflow file name** (`release.yml`) — so renaming that file silently
breaks publishing. The push is gated at **step** level on `secrets.NUGET_USER` (the nuget.org
profile name): a skipped *job* would skip `finalize` through `needs` and strand the release as a
permanent draft. A published NuGet version can only be unlisted, never replaced, which is why
`Check the package identity` asserts the packed file name before the push.

The published executable is named **`cifail`** (set via `<AssemblyName>`), not
`CiFail.Cli`. Self-contained/single-file props in `CiFail.Cli.csproj` are gated on
`'$(RuntimeIdentifier)' != ''`, so they only apply during `dotnet publish -r <rid>` and
never affect plain build/test/pack.

`cifail serve` (R7) is a real HTTP API in `src/CiFail.Server` (ASP.NET Core minimal API),
**size-gated into the full / Docker build only** via the `CIFAIL_SERVER` symbol (tied to
`-p:IncludeExternalDb=true`, exactly like `CIFAIL_EXTERNAL_DB`). The slim binaries get
neither the `serve` command nor ASP.NET Core. The Docker runtime base is therefore
`dotnet/aspnet:8.0` (not `runtime:8.0`). The Helm chart in `deploy/helm/cifail` runs it; see
`deploy/README.md`. Auth (R9): every route except those in `PublicPaths` requires `Authorization: Bearer
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
  `Program.cs` is one line; everything is in `CliApp.Build()` (a testable factory that takes an
  optional `IAnsiConsole` for Spectre's own help/version/parse output) plus `CliApp.Run()`.
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
  **R28** replaces the R12 static shell with a server-rendered dashboard: the project uses
  `Microsoft.NET.Sdk.Razor` (still a class library — FrameworkReference, hosted by the CLI) with
  `Dashboard/{App,Routes,Index,Login,Trends}.razor` + `Dash.cs` rendered via **Blazor static SSR**
  (`AddRazorComponents()`/`MapRazorComponents<App>()`; all CSS inlined in `App.razor`,
  `StaticWebAssetsEnabled=false`, no static assets). Auth is dual: `IsAuthorized` accepts
  `Authorization: Bearer` **or** the `cifail_auth` cookie (both constant-time via `MatchesAnyToken`);
  `WantsHtml` (GET + `Accept: text/html`) redirects unauthenticated browsers to `/login` while API
  clients get 401 + `WWW-Authenticate: Bearer`.
  `PublicPaths = {/healthz, /readyz, /login, /ui/login}` — the probes must stay public because
  **the kubelet sends no token**; putting one behind auth means the pod never becomes ready.
  `/healthz` is liveness and never touches the store (restarting a healthy pod because the DB
  blinked makes an outage worse); `/readyz` is readiness and queries it, so an unreachable
  database takes the pod out of rotation. Request bounds live in `ServeOptions`:
  `MaxLogBytes` (10 MB, enforced while reading via `ReadBoundedBody` — `Content-Length` is
  absent on chunked bodies and client-supplied anyway) and `MaxPageSize` (1000, via
  `ClampPageSize`). `POST /ui/login` is rate-limited (`LoginRateLimitPolicy`, 10/min per remote
  IP) because it is necessarily public and compares against the server token. **`MapEndpoints`
  takes the whole `ServeOptions`** — note the `/analyze` handler declares its own local named
  `options` (an `AnalysisOptions`), so limits are hoisted into locals at the top.
  Plain-HTML form posts land on `MapPost("/ui/login")` (validates the token, sets an
  HttpOnly/SameSite=Strict cookie) and `MapPost("/ui/resolve")` — both `.DisableAntiforgery()`
  (they're not Blazor forms; `UseAntiforgery()` only validates Blazor-opt-in posts). The `/ui/`
  prefix exists so the POSTs never collide with the `/login` Razor page route, and `/ui/login`
  must stay in `PublicPaths` (a signing-in browser has no cookie yet). JSON stays **PascalCase**
  (`AnalysisJson.Options`).
- **`tests/CiFail.Core.Tests`** — xUnit + FluentAssertions; fixtures in `fixtures/*.log`.
- **`tests/CiFail.Providers.Tests`** — shared store contract + Docker-gated DB tests.
- **`tests/CiFail.Server.Tests`** — boots a real serve instance on a random port (no Docker).
- **`tests/CiFail.Cli.Tests`** — drives `CliApp.Build()` in-process via `CliHarness`, which
  captures **stdout and stderr separately** (a single combined buffer can't test the stream
  split) and gives each test its own `CIFAIL_HOME`. It redirects the process console and
  `AnsiConsole.Console`, so everything is in the serial `CliCollection`.

### The analyze pipeline (`Core/Analysis/AnalysisService.cs`)

`Analyze()` is the orchestrator and the single entry point for the whole flow:

1. `Ingest/LogNormalizer.Build` — strip ANSI, normalize newlines, drop leading CI
   timestamps. Also exposes `Scrub()`, which collapses volatile tokens (paths,
   numbers, GUIDs) — used for fingerprints and similarity so cosmetic differences
   don't matter.
2. `Ingest/EcosystemDetector.Detect` — weighted-marker heuristic over the 14 ecosystems in
   `SupportedNamesText`; falls back to `generic`; overridable via `--type` (which now
   **rejects** an unknown value instead of silently auto-detecting). **Scoring counts markers,
   not occurrences**: each marker adds its weight at most once, because summing total match
   counts meant thirty `[ERROR] ` lines from a chatty logger outscored the markers that
   actually identified the log. Markers are `Strong` (3, effectively unique — `npm ERR!`,
   `Cargo.toml`) or `Weak` (1, suggestive but shared — `[ERROR] `, `gcc`, `gradlew`);
   `MinimumScore` = 2 so one weak marker never claims an ecosystem. Ties break by the explicit
   `Precedence` array (most specific first — Android before Java because every Android log is
   also a Gradle/Java log; Infra and Cpp last because everyone builds Docker images and native
   extensions). `Rank(log)` exposes the scores, which is what makes the tie-breaking testable.
3. `Rules/RuleEngine.Match` — runs applicable rules, returns matches ranked by
   confidence. Rules whose `ecosystem` is `generic` always apply; ecosystem-specific
   rules only apply when detected (or when ecosystem is undetected). Named regex
   capture groups are interpolated into the rule's `fix` template via `{name}`.
   **Every pattern compiles with `RuleEngine.MatchTimeout` (2s) and the cache is keyed on the
   pattern, not the rule id.** Rule packs are untrusted input — R14 loads them from the
   `.cifail/rules` of whatever repo you're in — so an unbounded regex is a hang, not a slow
   analysis. A timed-out or invalid rule is skipped and reported through the optional
   `diagnostics` sink, which `AnalysisService` surfaces as `Analysis.Warnings` and the CLI prints
   via `RuleDiagnostics.Report` (stderr, deduped; shared by `analyze` and `gate`).
   `Analysis.Warnings` is deliberately **not** in the `--json` DTO: it describes the run, not the
   answer.
   **Ecosystem inheritance (`RuleEngine.Inherits`):** `Android` and `Scala` also get the
   `java` rules, because those builds *are* JVM builds — an Android job that OOM'd used to
   get the vague `generic-oom` while `gradle-daemon-disappeared` sat unused in `java.yaml`,
   and the Kotlin rules (which live in that pack) never applied on the platform where most
   Kotlin is written. One-directional: Java must not pick up Android's aapt2 rules.
   **R34:** a `RuleMatch` also carries `LineNumber` (1-based), `ContextBefore`/`ContextAfter`
   (window size from `AnalysisOptions.ContextLines`, CLI `--context`, default 3) and
   `OccurrenceCount` (`regex.Matches`, capped at `RuleEngine.MaxCountedOccurrences` = 100).
   `ContextBlock`/`ContextStartLine` are the derived pair renderers use. This is what lets the
   console show numbered evidence, SARIF emit a real `region.startLine` + `snippet`, and the
   stored excerpt be re-read with its surroundings. The JSON fields are **omitted when empty**
   (null, not 0/[]) so an old consumer's document is byte-identical.
4. `Analysis/FingerprintBuilder` — `ruleId:hash` identity from scrubbed signature.
   **Note it hashes `MatchedLine`, not the context block** — widening the signature would
   re-fingerprint every existing failure, which is Phase 3's problem, not R34's.
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

### The new-failure gate (R29, `Core/Analysis/GateComputer.cs` + `GateBaseline.cs`)

`cifail gate <logs>` fails CI only on a fingerprint absent from a committed
`.cifail/baseline.txt`; `--update` rewrites the file. `GateComputer.Evaluate(observed, baseline)`
is a **pure** function (mirrors `StatsComputer`/`FailureClusterer`) returning
`{New, Known, Stale}` — it groups duplicate fingerprints into one `GateFinding` listing every
source, keeps findings in **first-seen** order (so the report reads like the log), and sorts
`Stale` (advisory only; it can never fail the gate). `GateBaseline` is the file format:
`Parse` skips comments/blank lines/junk (**an unparseable line must mean "not accepted", never
a crash**), `Render` sorts entries so a regenerated file diffs cleanly and flattens rule titles
so a newline in one can't forge a baseline line. JSON: `Output/GateJson.cs` (PascalCase).

**`gate` is deliberately storeless** — no `IAnalysisStore`, no git, no network, just
`AnalysisService.CreateDefault()`. Its memory is the committed file, so the verdict is identical
on a laptop and in a scratch container with a read-only home. Don't add store flags to it.
Unmatched failures are gated too (`unknown:<hash>`), which is the point. Input handling is
shared with `analyze` via `Cli/Commands/AnalysisInputs.cs` (`Read` + `BuildUnits`) so the two
can never disagree about what one failure is.

### History browsing + retention (R37, `Core/Storage/HistoryQuery.cs` + `Analysis/HistoryService.cs`)

`HistoryQuery` (limit/offset/since/repo/ecosystem/status/rule/search) + `HistoryPage`
(items, total, **truncated**) + `IHistoryQuery`, a **side-interface** like `IAnalysisStats`.
`HistoryService.Query(store, query)` routes to it when present, else `GetRecent(ScanLimit)` +
`HistoryQuery.Filter`. **Both paths share `Filter`, and a theory asserts they return identical
rows** — a filter that means one thing in SQL and another in LINQ is the bug this prevents.
SQLite implements it as real SQL with a `COUNT(*)` (so `Total` is exact and `Truncated` false);
the LIKE pattern **escapes `%`/`_`** or a search for "100%" matches everything.

The bug it fixes: `Index.razor` did `GetRecent(200)` + an in-memory `Where`, so **every dashboard
filter searched only the newest 200 rows** — an older resolved failure was unreachable and the
page said "No failures match" as though it had looked. The ecosystem dropdown now comes from
`StatsSnapshot.ByEcosystem` (a far wider window) rather than one page.

`IPrunableStore` + `cifail prune --older-than 90d [--include-open] [--dry-run]`: history had no
delete path at all. SQLite `VACUUM`s after deleting (deleting rows only frees pages *inside* the
file, and the point of pruning is usually the file size) and **clears `_corpusCache`** — a delete
can leave the `(count, max-id)` cache key unchanged while the contents differ.

**Log size cap (`LogNormalizer.Window`, `MaxCharacters` = 8 MB):** head+tail windowing with a
visible `ElisionMarker`, not a `Take(n)`. In CI the failure is at the *end* and the ecosystem
markers are at the *beginning*, so cutting either end alone throws away one or the other.
`LogDocument.RawText` is the **windowed** text — it must not pin a string we chose not to analyze.

**Notifications dispatch off the request thread** (`NotificationDispatcher.DispatchAsync`).
Filtering and dedupe stay synchronous so suppression keeps the caller's order; only the blocking
channel calls are deferred. The returned task is what keeps this testable — `ServeNotifyTests`
polls for the effect rather than assuming it landed before the HTTP response did.

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

### Server observability (R31, `Core/Output/PrometheusOutput.cs` + `Server/openapi.json`)

`GET /metrics` renders a `StatsSnapshot` as Prometheus **text exposition** — hand-rolled in
Core (like `SarifOutput`) rather than via a client library: it's a one-way export of an
already-computed snapshot, and Core has no ASP.NET packages. Everything is a **gauge**
(aggregates recomputed per scrape, not monotonic counters — calling them counters would
licence `rate()` over nonsense). Values format with `CultureInfo.InvariantCulture` (a decimal
comma makes the document unparseable) and label values are escaped. The per-fingerprint series
is capped at `MetricsTopFailures` = 10: a `fingerprint` label is unbounded and cardinality is
what kills a Prometheus server. `GET /openapi.json` serves an **embedded** hand-written
document (`EmbeddedResource` in the csproj — the CLI hosts this assembly from a single-file
binary with no content directory); a test walks its paths and asserts each still answers, since
nothing else stops it drifting. **Both routes are authenticated** — `/metrics` leaks rule ids
and failure counts, and Prometheus supports a bearer token in the scrape config.

### Dashboard panels (R32, `Server/Dashboard/Trends.razor`)

Three additions, all **static SSR — the dashboard must keep shipping zero `<script>`** (a test
asserts it): a failures-per-day **sparkline** (inline `<svg>` + `<polyline>`), the **noisiest
tests** card (`TestStatsService`, R26), and **cluster drill-down** via `<details>`/`<summary>`
with `/?sel={id}` links into the detail pane — no JS, no extra round trip. The data comes from
`StatsComputer.DailyBuckets` → `StatsSnapshot.Daily` (`IReadOnlyList<CountByDay>`, window size
`StatsQuery.DailyDays` = 30, mapped in `StatsJson.DayCountDto` as `yyyy-MM-dd`). **Every day in
the window is emitted, including the empty ones** — a chart built only from days that had
failures hides exactly the gaps it exists to show. `SparkPoints` scales y to the *window's own
peak* (a fixed ceiling flattens every quiet repo into a line along the bottom) and formats
coordinates with `CultureInfo.InvariantCulture` — a decimal comma silently turns `"12,3 45,6"`
into four coordinates. The `<svg>` carries `role="img"` + an `aria-label` summary, since a
sparkline is nothing to a screen reader. Cards that would be empty hide themselves (`Tests is
{ DistinctTests: > 0 }`) rather than sitting blank on every repo that only analyzes raw logs.

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
YAML list of `{ id, ecosystem, category, title, match (regex), confidence, fix, docs }`
plus the optional R36 fields `{ severity, requires, notMatch, ecosystems, enabled }` —
**all optional, all degrade to the pre-R36 behaviour when absent**:
- `severity` (`error|warning|note`) separates *how bad* from `confidence`'s *how sure*, and
  drives the SARIF level via `ReportFormatting.SarifLevel(confidence, severity)`; unset falls
  back to the confidence buckets.
- `requires` / `notMatch` are guards evaluated **only after `match` hits** (so a non-matching
  rule pays nothing), under the same `MatchTimeout`. An unusable guard leaves the rule
  **quiet** — failing open on a `notMatch` would admit exactly the match it exists to suppress.
- `ecosystems` is a list for genuinely cross-cutting rules (`AllEcosystems` merges it with the
  singular `ecosystem`). It deliberately does **not** replace `RuleEngine.Inherits`: that table
  is a fact about the ecosystems (an Android build *is* a JVM build), not about a rule, and
  encoding it per-rule means tagging every JVM rule and keeping them in step.
- `enabled: false` switches a rule off; the intended use is a 2-line user stub that silences a
  shipped rule by id. `RulePackValidator` skips the usual requirements for a disabled rule.
Packs are **embedded resources** (see `EmbeddedResource` glob in `CiFail.Core.csproj`),
loaded by `RulePackLoader` from the assembly; users can add their own, and a user rule with a
duplicate `id` overrides the embedded one. A malformed regex is skipped, never fatal. To add
coverage for a new failure, add a rule + a fixture + a row in `RulePackBreadthTests` — usually
no C# changes needed.

**Where user packs come from (`Core/Rules/RuleSearchPath.cs`):** `Resolve()` returns the load
order, most general → most explicit, and **later wins on a duplicate id**: `~/.cifail/rules` →
the nearest `.cifail/rules` walking up from the cwd → `config.yaml` `rules.paths` → `CIFAIL_RULES`
(a `PATH`-style list, appended to the file's by `ConfigLoader`) → `--rules <dir>` (`analyze`,
`gate`, `rules list`). The repo case is the reason it exists: a rule like "the determinism
contract is broken" belongs in the repo, and the old workaround (`CIFAIL_HOME=$PWD/.cifail`)
also relocated `history.db`. `RulePackLoader.LoadFrom(dirs)` is the multi-directory load;
`LoadAll()` (no argument) resolves the search path, `LoadAll(dir)` still means *only* that
directory, which is what keeps tests hermetic. Because cifail now loads packs it merely
**found**, `ParseUserPack` swallows an unparseable user pack (`rules validate` names it) —
a broken file in a checkout must not break analysis for everyone working in it.
`.cifail/` is the repo-side directory (`CiFailPaths.RepoDirectoryName`, shared with
`GateBaseline`); `CIFAIL_HOME` is the machine-side one.

**Every rule needs a fixture and a `docs:` URL, and both are enforced.**
`RulePackBreadthTests.Every_shipped_rule_has_a_fixture` fails the build for a rule with no
fixture (two thirds of the pack was in that state — an untested regex is an untested claim),
and `RulePackValidator` warns on a missing/non-http `docs:`. Each breadth row asserts the
expected rule **wins** the ranking, not merely that it matched: matching while losing to a
vaguer rule is indistinguishable from not working.

**Write fixtures from real tool output, not from the regex.** Three shipped rules could never
have matched anything real — `generic-command-not-found` had the shell's word order reversed,
`ruby-nomethod-error` used `.` across a newline (the engine compiles with `Multiline`, not
`Singleline`), and `composer-platform-requirement` had no room for composer's actual
"requires PHP extension ext-intl" wording. All three passed review; only a realistic fixture
caught them.

**A `fix` placeholder must be captured by EVERY top-level alternation branch of `match`.**
Enforced by `RulePackValidator` (error) and, end-to-end, by `RulePackBreadthTests` asserting no
rendered fix contains a leftover `{name}`. Eleven shipped rules violated this: a two-branch
`match` whose `fix` read the first branch's capture names, so a log hitting the other branch was
shown `tsc rejected {file}:{line}` verbatim. Note the **naive check does not catch it** — `file`
*is* defined in the pattern, just not in the branch that matched; the branch is the unit that has
to satisfy the fix. Two ways to comply: give both branches **duplicate group names** (.NET allows
this and it is the preferred fix — `generic.yaml:16` has always done it), or write a fix that
doesn't need the capture, which is the only option when a branch genuinely can't provide it (a
bare `ImagePullBackOff` doesn't name an image). The matched line is displayed anyway, so
"the line above names it" reads fine. Never use `name2`-suffixed groups to dodge duplicates.

**Two rules that both match one log must not share a confidence.** The sort is
`Score desc, then Rule.Id ordinal`, so an exact tie hands the decision to alphabetical order.
Fix it in the **data** — give the more specific rule the higher confidence — not by adding
signals to the engine. `RulePackBreadthTests.NoArbitraryWinner` asserts the top two matches on
every fixture differ, which is the only place it's detectable: two rules sharing a confidence is
perfectly fine until they both fire on the same thing.

**Ranking is deliberately still `Score = rule.Confidence`.** A positional/occurrence/corroboration
score was planned for R35 and dropped after measuring: across all 144 fixtures only **43** produce
more than one match, and the gap between the top two is ≥0.15 in most of them (exactly one tie,
now fixed). A bounded bonus would therefore almost never fire, and where it did it would flip a
winner — which re-fingerprints that failure and invalidates committed gate baselines — on an
unreviewed heuristic. Re-measure before revisiting: `analyze --json <fixtures>/*.log` and diff
the winner per fixture.

**Overlap is a design decision, not an accident.** A broad rule that restates a specific one
is noise in "other things cifail noticed" — `go-build-failed` excludes the errors the specific
Go rules explain via a negative lookahead, and `generic-docker-build` deliberately does *not*
cover what `infra.yaml` explains properly. A broad rule that *summarizes* (maven-build-failure,
terraform-error, xcode-build-failed) is useful and stays. When two rules can fire on one line,
the specific one must have the higher confidence — an equal confidence makes the winner
arbitrary (this was live between `go-undefined` and `go-version-mismatch`).

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
  `StoreSupport.WithStore(settings, store => ...)` — see the Output section.
- **Config validation:** the loader keeps `IgnoreUnmatchedProperties()` (an old cifail must not
  choke on a key a newer one added), so a malformed file now throws `ConfigException` (path +
  line/col, from `ConfigException.FromYaml`) instead of a raw `YamlException`, and
  `ConfigValidator.Validate(path)` / `ConfigLoader.Validate()` lints what the loader tolerates:
  unknown keys with a "did you mean" suggestion, plus value checks. **The known-key schema is
  derived by reflection over `CiFailConfig`** (`Shape()` + camelCase, matched *ordinally* because
  that's how YamlDotNet matches), so it can never drift when a setting is added — don't hand-write
  it. Surfaced by `cifail config`.

### `cifail config` / `doctor` (`Cli/Commands/ConfigCommand.cs`)

Answers "what is cifail actually doing?": version + build flavor (slim vs. full, via
`#if CIFAIL_EXTERNAL_DB`), every `CiFailPaths` value, `StoreRegistry`/`AiRegistry` availability,
and the effective database/AI/notification settings **each labelled with its provenance**
(`config.yaml` / the env-var name / `default` — `Resolve()` mirrors `ConfigLoader.Load`'s
precedence), plus the config diagnostics. `--json`, `--strict` (warnings also exit 4), `--path`.
**Invariant: it never prints a secret** — `Secret()` reports presence only, and a test plants a
password and a webhook URL and asserts neither appears on either stream. Keep it that way.

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
evolve independently — keep it stable.

**`analyze --json` is ALWAYS an array**, one element per analysis unit, `[]` included. It used to
emit a bare object for one input and an array for several, so the document's shape depended on how
many files a glob matched. Don't reintroduce the special case. The same goes for the empty result:
a report with no failing tests must still produce its `--json` document and write its
`--report-out` file — returning early there meant `upload-sarif` failed on a missing artifact on
exactly the runs where nothing was wrong.

**Two streams, one rule (`Cli/Output/CliConsole.cs`): stdout carries the answer, stderr carries
everything about the run.** Never call `AnsiConsole.*` from a command — use `CliConsole.Out` /
`CliConsole.Err`, or the `Error()` / `Warn()` / `Hint()` helpers (which take *markup*, so escape
interpolated values with `Markup.Escape`). Tables, `--json`, SARIF and the drafted YAML are the
answer; warnings, errors, and "nothing recorded yet" notes are not. Two deliberate exceptions
where the diagnostics *are* the answer: `rules validate`'s lint output and `rules test`'s
`no match.` stay on stdout. `--annotations` writes `::error::` to **stderr** (the Actions runner
scans both streams) so `--json --annotations` can't corrupt the JSON document.

**`Cli/Output/PathDisplay.cs`** — the report header's source label is elided **from the left**
(`Elide`), because the file/test name at the end is what identifies it. Spectre's own
truncation can't do this: a `Rule` title is word-wrapped and only the first line survives, so an
absolute path (one long word, and what every CI system passes) rendered as a bare ellipsis.

**`Cli/Output/Glyphs.cs`** — use `Glyphs.Check/Cross/Warning/Bullet/Ellipsis/Dash/Times/Dot`
instead of literal `✓✗⚠•…—×·` in console output; they degrade to ASCII when the console encoding
can't represent them. Files/payloads written as UTF-8 by construction (SARIF, Markdown,
notifications) keep the real characters. `CliApp.ConfigureConsole()` sets UTF-8 **including when
redirected** — via `UTF8Encoding(false)`, never `Encoding.UTF8`, whose preamble .NET would emit
as a BOM at the head of a pipe.

**Exit codes: `Cli/ExitCodes.cs` is the single taxonomy** (0 Ok · 1 Negative · 2 Usage ·
3 NotFound · 4 Config · 5 StoreUnavailable · 6 DependencyUnavailable · 7 NotUsable · 70 Internal ·
130 Canceled). `1` means "negative result", not "error" — that's what keeps `analyze`'s 0/1/2 and
`rules validate`'s 0/1 unchanged for `ci.yml` and `action.yml`, which branch on them. Never
return a bare int. `CliApp` installs a `SetExceptionHandler` (**not** `PropagateExceptions()`,
which bypasses it) mapping anything that escapes to a one-line stderr message + a code;
`CIFAIL_DEBUG=1` prints the stack trace instead.

**Store-backed commands must use `StoreSupport.WithStore(settings, store => ...)`**, not
`TryCreate` — `TryCreate` only guards *opening* the store, so an unreachable `--server` or an
expired token (failures that happen during the query) escaped as a raw exception. `WithStore`
also turns a 401 into a "pass `--server-token`" hint and maps a `ConfigException` to exit 4.

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
  licensed and incompatible with this MIT project. Do not upgrade it. `.github/dependabot.yml`
  ignores it by name for the same reason; that entry is a legal constraint, not a preference.
- Targets `net8.0` even though only the .NET 9 SDK is installed locally (builds fine).
  Consequences worth knowing: **EF Core majors are pinned below 10** (EF 10 targets net10.0
  only) and the Docker **runtime** base must stay `aspnet:8.0` — both are encoded as
  dependabot `ignore` rules so they don't reappear as a failing PR every week. The **SDK**
  image is free to move ahead.
- User-visible changes get an entry under `## [Unreleased]` in `CHANGELOG.md`. `SECURITY.md`
  documents the honest bits (history.db can contain secrets from the logs you analyzed;
  `serve` runs open without a token) — keep it accurate rather than reassuring.
- `.editorconfig` **describes** the existing style at `suggestion` severity; it is not a
  reformatting mandate, and nothing in it may turn into a build warning. Note the naming
  rules are order-sensitive: const/static-readonly (PascalCase) must precede the
  private-field rule (`_camelCase`), which would otherwise match them too.
- `*.log` is globally gitignored; sample and fixture logs are committed via explicit
  `!` un-ignore rules in `.gitignore` — keep new committed logs under `samples/` or a
  `fixtures/` dir.
- The `Analysis` type lives in namespace `CiFail.Core.Models` but there's also a
  `CiFail.Core.Analysis` namespace — inside the latter, refer to the type as
  `Models.Analysis`.
