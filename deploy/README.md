# Deploying cifail as a shared service

> **Status: `cifail serve` is implemented (R7).** The full / Docker build now ships a
> `cifail serve` HTTP API, and the Helm chart under `helm/cifail` runs it. Authentication
> is not yet built in (see open questions); don't expose it on an untrusted network until
> R9 lands. Today, you can also use cifail as a [CLI, Docker image, or CI step](../README.md).

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

| Method & path        | Body / query                          | Returns                              |
|----------------------|---------------------------------------|--------------------------------------|
| `GET /healthz`       | —                                     | `200 ok` (liveness/readiness)        |
| `POST /analyze`      | raw log text; `?type=&source=&noHistory=` | the analysis JSON (the `--json` DTO) |
| `GET /history`       | `?limit=N`                            | recent analyses                      |
| `GET /history/{id}`  | —                                     | one analysis, or `404`               |
| `POST /resolve/{id}` | `{ "note": "..." }`                   | the updated record                   |

Implemented now (R7) and what's still open:
- **Stateless**: ✅ the pod holds no state; a fresh store is opened per request and all
  persistence is the external DB, so it scales horizontally behind the Service.
- **Auth**: ⏳ not built in yet (**R9**). The endpoints expose *and write* failure history,
  so a real deploy needs at least a shared token / mTLS — keep it off untrusted networks.
- **Git correlation (R3)**: ⏳ the reconciler needs a working tree, which a central server
  doesn't have. Plan (**R11**): reconciliation stays client-side — the CLI talks to the
  server via the `http` store and runs the existing reconciler locally. The auto-resolution
  endpoints aren't exposed yet.
- **Similarity at scale**: ⏳ the corpus is loaded per request today; a service should push
  this into the DB (e.g. pgvector) before it's used by large teams (**R10**).

## How the chart maps to it

`helm/cifail` runs the **full** Docker image (`ghcr.io/sebhenn/cifail`, which bundles every
DB driver) with the command `cifail serve --port 8080`, wires `CIFAIL_DB_PROVIDER` /
`CIFAIL_DB_CONNECTION` from values/secret, and exposes it via a Service (+ optional
Ingress). Liveness/readiness probes hit `/healthz`.

```console
helm install cifail ./deploy/helm/cifail \
  --set database.provider=postgres \
  --set database.existingSecret=cifail-db \
  --set database.existingSecretKey=connection-string
```

Use `helm template ./deploy/helm/cifail` to review the rendered manifests before installing.
