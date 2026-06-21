# Deploying cifail as a shared service (design spike)

> **Status: design spike, not yet runnable.** The Helm chart under `helm/cifail` and the
> `cifail serve` API below describe the *planned* shape of a shared, team-wide cifail. The
> server mode (`cifail serve`) **does not exist yet**, so the chart will not produce a
> working pod until it lands. This directory exists to pin down the design and let the
> chart evolve alongside it. Today, use cifail as a [CLI, Docker image, or CI step](../README.md).

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

## Planned `cifail serve`

A thin HTTP API over the **existing `AnalysisService`** — no new analysis logic, just a
transport. Reuses the same `IAnalysisStore` (so it points at Postgres/MySQL/SQL Server/
Mongo exactly like the CLI) and the same `--json` DTO as the wire format.

| Method & path        | Body / query                          | Returns                              |
|----------------------|---------------------------------------|--------------------------------------|
| `GET /healthz`       | —                                     | `200 ok` (liveness/readiness)        |
| `POST /analyze`      | raw log text; `?type=&source=&noHistory=` | the analysis JSON (the `--json` DTO) |
| `GET /history`       | `?limit=N`                            | recent analyses                      |
| `GET /history/{id}`  | —                                     | one analysis, or `404`               |
| `POST /resolve/{id}` | `{ "note": "..." }`                   | the updated record                   |

Notes / open questions for the real implementation:
- **Auth**: none in the spike. Real deploy needs at least a shared token / mTLS, since the
  endpoint exposes (and writes) failure history.
- **Git correlation (R3)**: the reconciler needs a working tree, which a central server
  doesn't have. Options: keep reconciliation client-side (CLI/CI calls `POST /resolve`), or
  have clients send git facts (`repo_id`, `commit`, ancestry) to a `POST /reconcile`. TBD.
- **Similarity at scale**: the corpus is loaded per request today; a service should push
  this into the DB (e.g. pgvector) before it's used by large teams.
- **Stateless**: the pod holds no state; all persistence is the external DB, so it scales
  horizontally behind the Service.

## How the chart maps to it

`helm/cifail` runs the **full** Docker image (`ghcr.io/sebhenn/cifail`, which bundles every
DB driver) with the command `cifail serve --port 8080`, wires `CIFAIL_DB_PROVIDER` /
`CIFAIL_DB_CONNECTION` from values/secret, and exposes it via a Service (+ optional
Ingress). Liveness/readiness probes hit `/healthz`.

```console
# Once `cifail serve` exists:
helm install cifail ./deploy/helm/cifail \
  --set database.provider=postgres \
  --set database.existingSecret=cifail-db \
  --set database.existingSecretKey=connection-string
```

Until then, `helm template ./deploy/helm/cifail` is useful to review the rendered manifests,
but a deployed pod will crash-loop on the missing `serve` subcommand — by design.
