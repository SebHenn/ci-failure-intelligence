# Deploying cifail as a shared service

> **Status: `cifail serve` is implemented (R7) and authenticated (R9).** The full / Docker
> build ships a `cifail serve` HTTP API protected by a shared bearer token, and the Helm
> chart under `helm/cifail` runs it. Today, you can also use cifail as a
> [CLI, Docker image, or CI step](../README.md).

## Why a service at all?

Everything cifail does is local-first and works great per-developer and per-CI-job. But two
features only reach their full value when **many** people/pipelines share one backend:

- **History & similarity** across a whole team ("has *anyone* hit this before?").
- **Auto-resolutions** aggregated across branches and CI runs.

That already works today by pointing every cifail at the **same external database**
(`CIFAIL_DB_PROVIDER` / `CIFAIL_DB_CONNECTION`, see the README). A long-running service adds:
a single network endpoint (no DB credentials handed to every runner), centralized
similarity (eventually server-side vectors instead of loading the corpus per call), and a
place to host a future web UI.

## `cifail serve`

A thin HTTP API over the **existing `AnalysisService`** — no new analysis logic, just a
transport. Reuses the same `IAnalysisStore` (so it points at Postgres/MySQL/SQL Server/
Mongo exactly like the CLI) and the same `--json` DTO as the wire format. Run it with
`cifail serve --port 8080` (full / Docker build only). Clients can also point the
`history`/`resolve` commands at it with `cifail history --server http://host:8080`.

All routes except `/healthz` and the sign-in flow (`/login`, `POST /ui/login`) require
`Authorization: Bearer <token>` — or the dashboard's auth cookie — when the server is
started with a token (see **Auth** below).

| Method & path        | Body / query                          | Returns                              |
|----------------------|---------------------------------------|--------------------------------------|
| `GET /`              | —                                     | the web dashboard (R28, cookie-auth) |
| `GET /healthz`       | —                                     | `200 ok` (liveness/readiness, open)  |
| `POST /analyze`      | raw log text; `?type=&source=&noHistory=` | the analysis JSON (the `--json` DTO) |
| `GET /history`       | `?limit=N`                            | recent analyses                      |
| `GET /history/{id}`  | —                                     | one analysis, or `404`               |
| `GET /repos/{repoId}/open` | —                               | open failures for one repo (R11)     |
| `POST /resolve/{id}` | `{ "note": "..." }`; `?source=auto&commit=<sha>` for auto | the updated record |
| `GET /stats`         | `?since=&repo=&top=`                  | aggregated stats (R16)               |
| `GET /clusters`      | `?threshold=&since=&repo=&top=&all=`  | near-duplicate failure groups (R25)  |
| `GET /metrics`       | —                                     | Prometheus text exposition (R31)     |
| `GET /openapi.json`  | —                                     | OpenAPI 3.0 description of this API  |

### Scraping `/metrics`

The metrics are **gauges**, not counters: they're aggregates recomputed from history on each
scrape (`cifail_failures_total`, `_open`, `_resolved`, `_unmatched`, `_by_ecosystem`,
`cifail_recurrence_rate`, `cifail_mean_time_to_resolution_seconds`, `cifail_flaky_failures`,
and the top recurring `cifail_failure_occurrences`). They come from the same `StatsService`
as `GET /stats` and `cifail stats`, so a Grafana board and the CLI can't disagree.

`/metrics` is **authenticated like every other route** — it exposes rule ids, ecosystems and
failure counts. Prometheus supports a bearer token directly:

```yaml
scrape_configs:
  - job_name: cifail
    authorization:
      credentials: <your CIFAIL_SERVER_TOKEN>
    static_configs:
      - targets: ['cifail:8080']
```

Only the top 10 fingerprints get a per-failure series. A `fingerprint` label is unbounded,
and cardinality is what kills a Prometheus server.

What the server does today (release history lives in [CHANGELOG.md](../CHANGELOG.md);
the `R<n>` markers are this repo's internal milestone ids, cross-referenced from `CLAUDE.md`):
- **Stateless**: ✅ the pod holds no state; a fresh store is opened per request and all
  persistence is the external DB, so it scales horizontally behind the Service.
- **Auth**: ✅ (**R9**) a shared bearer token, set via `CIFAIL_SERVER_TOKEN` or
  `serve --token`, is required on every route except `/healthz` (constant-time compared).
  Started without a token, serve runs open and logs a loud warning. Clients
  (`--server`) send it via `--server-token` / `CIFAIL_SERVER_TOKEN`.
  **R20** adds two production options: **per-client named tokens** for individual
  rotation/revocation (`CIFAIL_SERVER_TOKENS` comma list and/or `serve --tokens-file`,
  each constant-time compared) and opt-in **mutual TLS** (`serve --client-ca <pem>
  --tls-cert <pfx>` — the server terminates HTTPS and rejects, at the handshake, any client
  cert that doesn't chain to the CA). See "Mutual TLS" below for the chart wiring.
- **Git correlation (R3)**: ✅ (**R11**) resolved as planned — **reconciliation stays on the
  client** (it has the working tree). The server exposes open failures
  (`GET /repos/{repoId}/open`) and accepts auto-resolutions (`POST /resolve/{id}?source=auto&
  commit=<sha>`, which never overwrites a manual one); the unchanged `ResolutionReconciler`
  runs against the remote `http` store. Use `cifail reconcile --server <url>` (and the
  `cifail init` git hooks work the same way).
- **Web dashboard**: ✅ (**R28**) rendered server-side at `/` (Blazor static SSR, embedded in
  the server assembly, no separate deploy) — browse/filter failures and resolve them. Browsers
  sign in once at `/login`; an HttpOnly `cifail_auth` cookie (SameSite=Strict, Secure over
  https) then authorizes the dashboard, while API clients keep using the Bearer token.
  **R32** adds a failures-per-day sparkline (last 30 days, quiet days included), a noisiest-tests
  card fed by the per-test flakiness data, and cluster drill-down that expands to the failures in
  each group. It still ships **no JavaScript** — the chart is inline SVG and the drill-down is
  `<details>` — so the dashboard works behind a strict CSP and with scripting disabled.
- **Similarity at scale**: ✅ (**R10**, opt-in) the default is still in-app TF-IDF, but a
  vector-capable store can do nearest-neighbour search in the database. Use the `pgvector`
  provider (`CIFAIL_DB_PROVIDER=pgvector`, PostgreSQL + the `vector` extension) and enable
  embeddings (`CIFAIL_AI_EMBEDDINGS=1`); cifail embeds each failure via the configured AI
  provider (Ollama by default) and queries with an HNSW cosine index. The embedding size must
  match the column — set `CIFAIL_AI_EMBED_DIM` if it isn't 768.
- **Notifications**: ✅ (**R13**, opt-in; channels extended in **R27**) the server can alert
  Slack, a generic webhook, Discord, Microsoft Teams, email, or open a GitHub issue when a
  failure is new, recurs, or is resolved. Configure a `notifications:` block (events,
  `slackWebhookUrl`, `webhookUrl`, `discordWebhookUrl`, `teamsWebhookUrl`, `dedupeSeconds`,
  `smtp`, `github`) in the config file; the webhook URLs can also come from the matching
  `CIFAIL_NOTIFY_*_URL` env vars, the SMTP password from `CIFAIL_SMTP_PASSWORD`, and the
  GitHub token from `GITHUB_TOKEN`, so no secrets live in the file. The GitHub channel opens
  one issue per distinct failure and comments on recurrence (idempotent via a hidden
  fingerprint marker). Notifications fire **only server-side**, are off until a channel is
  set, dedupe per `(event, fingerprint)`, and are best-effort (a broken channel never affects
  analysis).

## How the chart maps to it

`helm/cifail` runs the **full** Docker image (`ghcr.io/sebhenn/cifail`, which bundles every
DB driver) with the command `cifail serve --port 8080`, wires `CIFAIL_DB_PROVIDER` /
`CIFAIL_DB_CONNECTION` and `CIFAIL_SERVER_TOKEN` from values/secret, and exposes it via a
Service (+ optional Ingress). Liveness/readiness probes hit `/healthz` (open, no token).

```console
helm install cifail ./deploy/helm/cifail \
  --set database.provider=postgres \
  --set database.existingSecret=cifail-db \
  --set database.existingSecretKey=connection-string \
  --set auth.existingSecret=cifail-auth \
  --set auth.existingSecretKey=token
```

Auth is on by default (`auth.enabled=true`); provide the token via `auth.existingSecret`
(recommended) or `auth.token` (dev only, chart-created Secret). Set `auth.enabled=false` to
run open on a trusted network.

### Mutual TLS (R20)

To require client certificates, put the CA bundle and the server PFX in a Secret and enable
`mtls`:

```console
kubectl create secret generic cifail-tls \
  --from-file=ca.pem=ca.pem \
  --from-file=server.pfx=server.pfx

helm install cifail ./deploy/helm/cifail \
  --set mtls.enabled=true \
  --set mtls.existingSecret=cifail-tls
```

The chart mounts the Secret at `mtls.mountPath` (default `/etc/cifail/tls`) and appends
`--client-ca`/`--tls-cert`. If the PFX is encrypted, add its password to the same Secret and set
`mtls.passwordSecretKey` (injected as `CIFAIL_TLS_PASSWORD`). Because the kubelet probes can't
present a client cert, the liveness/readiness probes switch to a TCP check while mTLS is on. mTLS
and the bearer token are independent — enable either or both.

Use `helm template ./deploy/helm/cifail` to review the rendered manifests before installing.
