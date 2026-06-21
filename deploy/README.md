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

All routes except `/healthz` require `Authorization: Bearer <token>` when the server is
started with a token (see **Auth** below).

| Method & path        | Body / query                          | Returns                              |
|----------------------|---------------------------------------|--------------------------------------|
| `GET /`              | —                                     | the bundled web dashboard (R12, open)|
| `GET /healthz`       | —                                     | `200 ok` (liveness/readiness, open)  |
| `POST /analyze`      | raw log text; `?type=&source=&noHistory=` | the analysis JSON (the `--json` DTO) |
| `GET /history`       | `?limit=N`                            | recent analyses                      |
| `GET /history/{id}`  | —                                     | one analysis, or `404`               |
| `GET /repos/{repoId}/open` | —                               | open failures for one repo (R11)     |
| `POST /resolve/{id}` | `{ "note": "..." }`; `?source=auto&commit=<sha>` for auto | the updated record |

Implemented now (R7 + R9 + R11) and what's still open:
- **Stateless**: ✅ the pod holds no state; a fresh store is opened per request and all
  persistence is the external DB, so it scales horizontally behind the Service.
- **Auth**: ✅ (**R9**) a shared bearer token, set via `CIFAIL_SERVER_TOKEN` or
  `serve --token`, is required on every route except `/healthz` (constant-time compared).
  Started without a token, serve runs open and logs a loud warning. Clients
  (`--server`) send it via `--server-token` / `CIFAIL_SERVER_TOKEN`. mTLS is a possible
  future hardening on top of the token.
- **Git correlation (R3)**: ✅ (**R11**) resolved as planned — **reconciliation stays on the
  client** (it has the working tree). The server exposes open failures
  (`GET /repos/{repoId}/open`) and accepts auto-resolutions (`POST /resolve/{id}?source=auto&
  commit=<sha>`, which never overwrites a manual one); the unchanged `ResolutionReconciler`
  runs against the remote `http` store. Use `cifail reconcile --server <url>` (and the
  `cifail init` git hooks work the same way).
- **Web dashboard**: ✅ (**R12**) a single bundled page at `/` (embedded in the server
  assembly, no separate deploy) — browse/filter failures and resolve them. The shell is public
  so it can load and collect a token; all its data calls hit the authenticated API.
- **Similarity at scale**: ⏳ the corpus is loaded per request today; a service should push
  this into the DB (e.g. pgvector) before it's used by large teams (**R10**).

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

Use `helm template ./deploy/helm/cifail` to review the rendered manifests before installing.
