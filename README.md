# cifail — understand why your build failed

[![CI](https://github.com/SebHenn/ci-failure-intelligence/actions/workflows/ci.yml/badge.svg)](https://github.com/SebHenn/ci-failure-intelligence/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Linux%20%7C%20Windows-lightgrey.svg)](#install)
[![Offline-first](https://img.shields.io/badge/offline-first-success.svg)](#what-problem-does-this-solve)

Builds and tests fail with walls of confusing log output. **cifail** reads that output
and tells you, in plain English:

1. **What broke** — the one thing that actually caused the failure.
2. **How to fix it** — concrete next steps, not just the error code.
3. **Whether you've hit it before** — and how you fixed it last time.

It runs entirely on your own machine. Nothing is uploaded anywhere, it works offline,
and it doesn't care what language your project is in.

```console
$ dotnet build 2>&1 | cifail analyze

── cifail · stdin ───────────────────────────────────────────────
╭─ What broke: NuGet package not found  (dotnet · dependency · high confidence) ─╮
│ How to fix it                                                                  │
│ Package 'Newtonsoft.Jsn' was not found in any configured NuGet source.         │
│ Check the package name/version for typos, and confirm the right source is      │
│ configured in nuget.config (e.g. nuget.org or a private feed).                 │
│                                                                                │
│ The line that gave it away:                                                    │
│ error NU1101: Unable to find package Newtonsoft.Jsn …                          │
╰────────────────────────────────────────────────────────────────────────────────╯
Saved as #1. Once you've fixed it, record how so future-you remembers:
  cifail resolve 1 --note "what fixed it"
```

> `2>&1` just means "send error messages to the same place as normal output", so cifail
> sees the whole log. The `|` ("pipe") feeds that output into cifail.

---

## Contents

- [What problem does this solve?](#what-problem-does-this-solve)
- [Install](#install)
- [Use it](#use-it)
- [The three commands you'll actually use](#the-three-commands-youll-actually-use)
  - [See the trends: `cifail stats`](#see-the-trends-cifail-stats)
  - [It remembers your fixes — often without being told](#it-remembers-your-fixes--often-without-being-told)
  - [Handy options for `analyze`](#handy-options-for-analyze)
- [Use cifail in CI](#use-cifail-in-ci) — [GitHub Actions](#github-actions-the-ready-made-action) · [GitLab CI](#gitlab-ci) · [Exit codes](#exit-codes)
- [What do the words mean?](#what-do-the-words-mean)
- [Check your setup: `cifail config`](#check-your-setup-cifail-config)
- [Where your data is kept](#where-your-data-is-kept)
- [Use a shared database (optional)](#use-a-shared-database-optional)
- [Run a shared server (optional)](#run-a-shared-server-optional)
- [Contributing](#contributing)
- [Project status](#project-status)
- [License](#license)

---

## What problem does this solve?

A failing CI log can be hundreds of lines long, and the line that matters is buried
somewhere in the middle. Figuring out *which* line, and what to do about it, takes
experience. cifail does that first pass for you:

- It knows the common failure patterns for **.NET, Node/npm, Python/pip, Java, Go, Rust,
  Ruby, PHP, C/C++, Swift, Android, and infra (Docker/Terraform)**, plus generic CI errors,
  so it can point straight at the cause.
- It **remembers** every failure you analyze. The next time a similar one shows up, it
  reminds you — including the fix you wrote down last time.

Think of it as a teammate who has seen a lot of broken builds and remembers all of them.

---

## Install

cifail is a single self-contained binary — **no .NET, no runtime, nothing else to
install.** Pick your platform:

**macOS / Linux** (one line):

```console
curl -fsSL https://raw.githubusercontent.com/SebHenn/ci-failure-intelligence/main/scripts/install.sh | bash
```

**Windows** ([Scoop](https://scoop.sh)):

```console
scoop install https://github.com/SebHenn/ci-failure-intelligence/releases/latest/download/cifail.json
```

**.NET global tool** (if you already have the .NET 8+ SDK):

```console
dotnet tool install --global cifail
```

**Manual:** grab the binary for your OS from the
[Releases page](https://github.com/SebHenn/ci-failure-intelligence/releases), unzip it,
and put it on your `PATH`. Every archive ships a `.sha256` next to it, and each release has
an aggregate `SHA256SUMS` — verify before you run:

```console
sha256sum -c cifail-0.2.0-linux-x64.tar.gz.sha256   # macOS: shasum -a 256 -c
```

**Homebrew:** every release ships a rendered `cifail.rb` formula, but there is no official
tap yet — copy it into your own tap, or use the `curl … install.sh` line above.

**Docker** (no install — and the image bundles every database driver):

```console
# Analyze a log in the current folder:
docker run --rm -v "$PWD:/work" ghcr.io/sebhenn/cifail analyze build.log

# Keep history between runs by mounting a volume at /data:
docker run --rm -v "$PWD:/work" -v cifail-data:/data ghcr.io/sebhenn/cifail history
```

The Docker image is the **full** build: PostgreSQL, MySQL/MariaDB, SQL Server and MongoDB
support are all included (the standalone binaries stay SQLite-only to keep them small).

> Prefer a CI pipeline? See [Use cifail in CI](#use-cifail-in-ci) below.

## Use it

Run your build or test command and pipe its output into cifail:

```console
dotnet build 2>&1 | cifail analyze
npm install   2>&1 | cifail analyze
pytest        2>&1 | cifail analyze
go build ./... 2>&1 | cifail analyze
```

Or save a log to a file first and analyze that:

```console
npm test > test.log 2>&1
cifail analyze test.log
```

Want to see it work right now without a broken build of your own? The repo ships
example logs:

```console
cifail analyze samples/nuget-nu1101.log
```

---

## The three commands you'll actually use

| Command | What it does |
|---------|--------------|
| `cifail analyze <log>` | Look at a log and explain the failure. This is the main one. |
| `cifail history` | Show the failures you've analyzed before. Each has a number (its id). |
| `cifail resolve <id> --note "..."` | Write down how you fixed failure number `<id>`, so cifail can remind you next time you hit something similar. |

There's also `cifail rules list` to see every failure pattern cifail can recognize, and
`cifail --help` (or `cifail analyze --help`) for the full list of options.

### See the trends: `cifail stats`

Once you've got some history, `cifail stats` turns it into signal — your most common
failures, how many are still open vs resolved, a breakdown by ecosystem, how long fixes
take on average, and a **flaky** flag for failures that were resolved and then came back
(so the fix didn't really stick).

```bash
cifail stats                 # all-time summary
cifail stats --since 7d      # just the last week (also: 24h, 2w, or a date)
cifail stats --top 5 --json  # machine-readable, top 5 recurring failures
cifail stats --server http://your-host:8080   # against a shared serve instance
```

Got a lot of history? `cifail clusters` groups near-duplicate failures together, so you
see a handful of distinct root causes instead of a long flat list:

```bash
cifail clusters                    # the biggest groups of similar failures
cifail clusters --threshold 0.6    # tighter grouping (higher = more similar required)
cifail clusters --all --json       # include one-offs, machine-readable
```

If you feed cifail JUnit/TRX reports (see [`analyze --format`](#handy-options-for-analyze)),
`cifail stats --tests` ranks the noisiest and flakiest individual tests. (cifail records
failures, not passes, so this is "recurring & flaky", not a true pass-rate.)

```bash
cifail stats --tests             # flakiest / noisiest tests
cifail stats --tests --json      # machine-readable
```

### It remembers your fixes — often without being told

When you run cifail inside a git repository, it tags each failure with the commit you were
on. Later, when you've moved past that commit and the failure no longer happens, cifail
marks it **resolved automatically** and credits the commit that fixed it — so the "how did
we fix this last time?" history fills itself in. In `cifail history` these show up as
`✓ auto` (versus `✓` for ones you wrote with `cifail resolve`). A manual note always wins.

- It happens on its own every time you `cifail analyze` inside a repo.
- `cifail reconcile` runs the same check on demand (e.g. after merging).
- `cifail init` installs git hooks so it runs hands-off on every commit and merge.
- `cifail analyze --no-git` turns the whole thing off for a run.

### Handy options for `analyze`

- `--json` — print the result as JSON instead of a panel. Useful inside CI pipelines or
  other scripts.
- `--type dotnet|node|python|java|go|rust|ruby|php|cpp|swift|android|infra|generic` — tell
  cifail what kind of log this is, if it guesses wrong.
- `--format auto|log|junit|trx` — feed cifail a **structured test report** instead of a raw
  log. `auto` (the default) sniffs JUnit XML and .NET TRX by their extension/root element;
  each *failing test* is analyzed on its own, so similarity and history work per test. A
  report with no failures exits 0.
- `--annotations` — when running under GitHub Actions, also print `::error::` annotations for
  each failing test so they show up inline on the PR (pairs with `--format junit|trx`).
- `--ai` — when cifail isn't sure (no rule matched, or only a low-confidence one), also ask
  an AI model for a root cause and fix. **Off by default**, and only consulted when the rules
  fall short — so cifail stays fast and fully offline unless you opt in. If the model is
  unavailable the analysis still works; you just get the rules-only result.
  - The default backend is a **local [Ollama](https://ollama.com)** model (nothing leaves your
    machine). Install Ollama and pull a model (e.g. `ollama pull llama3.2`), then
    `cifail analyze --ai build.log`.
  - Pick the backend with `--ai-provider ollama|anthropic|openai` and the model with
    `--ai-model <name>`. The hosted backends are opt-in and read their API key from the
    `CIFAIL_AI_KEY` environment variable (they make outbound network calls).
  - Defaults live under an `ai:` section in `~/.cifail/config.yaml`; env overrides are
    `CIFAIL_AI_PROVIDER` / `CIFAIL_AI_MODEL` / `CIFAIL_AI_URL` / `CIFAIL_AI_KEY`.
- `--no-history` — analyze without saving this run to history.

---

## Use cifail in CI

### GitHub Actions (the ready-made action)

Capture a failing step's output, then let the action explain it — the analysis shows up in
the job's **step summary**. No install needed; it runs the Docker image for you.

```yaml
- name: Build
  run: dotnet build 2>&1 | tee build.log

- name: Explain failures
  if: failure()
  uses: SebHenn/ci-failure-intelligence@v1
  with:
    log: build.log
```

Inputs: `log` (file to analyze), `args` (extra flags, e.g. `--type node`), `image` (pin a
version instead of `:latest`), `summary` (write to the step summary, default `true`),
`fail` (propagate cifail's exit code; off by default since it runs after a failed build),
`comment` (post the analysis as a pull-request comment, default `false`), and `sarif`
(also write a SARIF report to that path). For shared history, set `CIFAIL_DB_PROVIDER` /
`CIFAIL_DB_CONNECTION` in the job env.

To comment on the PR, give the step a token with `pull-requests: write` and set
`comment: true` — the comment is **idempotent** (updated in place on re-runs, not duplicated):

```yaml
- name: Explain failures
  if: failure()
  uses: SebHenn/ci-failure-intelligence@v1
  with:
    log: build.log
    comment: true
  env:
    GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

#### Code Scanning (SARIF)

Set `sarif` to a path and upload it — cifail's findings then show up in the repo's
**Security → Code scanning** tab, tracked across runs by failure fingerprint:

```yaml
- name: Explain failures
  if: failure()
  uses: SebHenn/ci-failure-intelligence@v1
  with:
    log: build.log
    sarif: cifail.sarif

- name: Upload SARIF
  if: always()           # upload even though the build failed
  uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: cifail.sarif
```

Or run the CLI directly: `cifail analyze --report sarif --report-out cifail.sarif build.log`
(use `--report markdown` for a step-summary/PR-comment-friendly document instead).

### GitLab CI

Include the **CI/CD component** and point it at your log. It adds a `cifail-analyze` job that
runs only `on_failure` and never fails the pipeline itself, plus an optional `cifail-mr-comment`
job that posts the analysis on the merge request:

```yaml
include:
  - component: $CI_SERVER_FQDN/SebHenn/ci-failure-intelligence/cifail@main
    inputs:
      log: build.log
      comment: true        # post the analysis on the MR (needs CIFAIL_GITLAB_TOKEN)

build:
  stage: build
  script:
    - your-build-command 2>&1 | tee build.log
```

Inputs: `stage` (default `test`), `log`, `image`, `args`, `fail` (gate the pipeline; default
off), `comment` (post an idempotent MR note; default off), and `comment_image`. The MR comment
needs a `CIFAIL_GITLAB_TOKEN` CI/CD variable (a project/group token with `api` scope); it's
updated in place on re-runs. For shared history, set `CIFAIL_DB_PROVIDER` /
`CIFAIL_DB_CONNECTION` as CI/CD variables.

<details>
<summary>Older GitLab template (back-compat)</summary>

The pre-component hidden-job template still works if you can't use components:

```yaml
include:
  - remote: 'https://raw.githubusercontent.com/SebHenn/ci-failure-intelligence/main/ci-templates/gitlab.yml'

explain-failures:
  extends: .cifail
  needs: ["build"]
  variables:
    CIFAIL_LOG: build.log
```

</details>

### Anywhere else (plain pipe)

If you've installed the binary (or are in a `cifail` container), just pipe into it:

```yaml
- name: Explain failures
  if: failure()
  run: cifail analyze build.log
```

### Exit codes

Scripts can branch on *why* something failed, not just whether it did:

| Code | Meaning |
|-----:|---------|
| `0` | Success. For `analyze`, every log matched a known pattern. |
| `1` | Ran fine, negative answer: `analyze` found nothing it recognizes, `rules test` didn't match, `rules validate` found errors. Not a tool failure. |
| `2` | Bad invocation — unknown flag or value, missing argument, unreadable input. |
| `3` | Not found — no analysis with that id, no rule with that id. |
| `4` | Your `~/.cifail/config.yaml` is broken or invalid. Run `cifail config`. |
| `5` | The history store is unreachable: bad connection string, or a `--server` that's down or rejected the token. |
| `6` | An optional dependency is missing (no local AI model, not a git repository). |
| `7` | Produced a result that isn't usable as-is (a drafted rule that failed validation). |
| `70` | Unexpected error. Set `CIFAIL_DEBUG=1` to see the stack trace, then please file an issue. |
| `130` | Interrupted (Ctrl-C). |

`0`, `1` and `2` are stable and safe to depend on. Two related guarantees:

- **stdout is only ever the answer** — the report, `--json`, SARIF, the drafted YAML. Warnings,
  errors and hints all go to **stderr**, so `cifail analyze --json build.log | jq` never breaks,
  even when cifail has something to complain about.
- **cifail never prints a stack trace** for a problem it understands. If you see one, it's a bug.

---

## What do the words mean?

cifail tries to keep things plain, but a few terms show up:

- **ecosystem** — the kind of project the log came from: `dotnet`, `node`, `python`,
  `java`, `go`, `rust`, `ruby`, `php`, `cpp`, `swift`, `android`, `infra` (Docker/Terraform),
  or `generic` (anything else, plus cross-cutting failures like timeouts, disk-full, DNS/TLS,
  and rate limits). cifail guesses this automatically.
- **confidence** — how sure cifail is about the cause: **high**, **medium**, or **low**.
  Low confidence means "this is my best guess — double-check it".
- **rule / pattern** — a single known failure cifail can recognize (for example, "NuGet
  package not found"). The set of all of them is what `cifail rules list` shows.

---

## Check your setup: `cifail config`

When something isn't behaving the way you expect, this answers "what is cifail actually
doing?" in one screen:

```bash
cifail config          # or: cifail doctor
cifail config --json   # machine-readable
cifail config --strict # also fail on warnings (for CI)
```

It shows the version and build flavour (slim vs. the full Docker build), every path it uses,
which database providers this binary actually has, and the effective database / AI /
notification settings — each one labelled with **where it came from**: `config.yaml`, the name
of an environment variable, or `default`. That last column is usually the answer to "I edited
the config file and nothing changed", since environment variables win over the file.

It also lints `config.yaml` and reports what the loader otherwise ignores in silence. A
misspelled key is the classic one:

```
warning: line 2: unknown setting 'notifications.slackWebhoookUrl', so it is ignored — did you mean 'slackWebhookUrl'?
```

cifail deliberately tolerates unknown keys at load time (so an older cifail never chokes on a
config written for a newer one) — which means a typo like that silently does nothing until you
run this.

**It never prints secrets.** Connection-string passwords, webhook URLs and tokens are reported
only as `set` / `not set`, next to the name of the variable they come from — so the output is
safe to paste into a bug report.

---

## Where your data is kept

cifail stores your history and any custom patterns in a folder called `.cifail` in your
home directory (`~/.cifail`). It never leaves your machine. If you want it somewhere
else (for example, one per project), set the `CIFAIL_HOME` environment variable to a
folder of your choice.

By default history lives in a local SQLite file — zero setup, nothing to run. If your
team wants to **share** history, you can point cifail at an external database instead.

---

## Use a shared database (optional)

Out of the box cifail uses local SQLite and needs no configuration. To share history
across a team or CI, point it at PostgreSQL, MySQL/MariaDB, SQL Server, or MongoDB.

Pick a backend in any of three ways (highest priority first):

```console
# 1. Command-line flags (any command that touches history):
cifail analyze build.log \
  --db-provider postgres \
  --db-connection "Host=db;Port=5432;Username=ci;Password=secret;Database=cifail"

# 2. Environment variables (handy in CI):
export CIFAIL_DB_PROVIDER=postgres
export CIFAIL_DB_CONNECTION="Host=db;Port=5432;Username=ci;Password=secret;Database=cifail"

# 3. A config file at ~/.cifail/config.yaml:
```

```yaml
# ~/.cifail/config.yaml
database:
  provider: postgres   # sqlite (default) | postgres | mysql | sqlserver | mongodb
  connectionString: "Host=db;Port=5432;Username=ci;Password=secret;Database=cifail"
```

The connection string is whatever the database engine expects (cifail just hands it to
the driver). cifail creates its table(s) on first use — no migrations to run.

> **Which build do you have?** External databases ship in the **Docker image** and the
> full build. The standalone single-file binaries stay SQLite-only to keep them small —
> if you ask one of them for `postgres`/`mysql`/`sqlserver`/`mongodb` it will tell you so.
> To try the external providers locally there's a `docker-compose.test.yml` with all four
> engines pre-configured.

---

## Run a shared server (optional)

Instead of giving every machine the database connection string, you can run **one** cifail
as an HTTP service and point everyone at it. The Docker image includes this `serve` mode:

```console
docker run --rm -p 8080:8080 \
  -e CIFAIL_DB_PROVIDER=postgres \
  -e CIFAIL_DB_CONNECTION="Host=db;Username=ci;Password=secret;Database=cifail" \
  -e CIFAIL_SERVER_TOKEN="a-long-random-secret" \
  ghcr.io/sebhenn/cifail serve --port 8080
```

It exposes a small JSON API (`GET /healthz`, `POST /analyze`, `GET /history`,
`GET /history/{id}`, `GET /repos/{repoId}/open`, `POST /resolve/{id}`) — `POST /analyze`
returns the exact same shape as `cifail analyze --json`. Your CLI can browse and annotate that
shared history, and reconcile fixed failures, without any database credentials:

```console
# Analyze a log against the shared server (it runs the pipeline + stores the result):
cifail analyze build.log --server http://your-host:8080 --server-token "a-long-random-secret"
cifail history --server http://your-host:8080 --server-token "a-long-random-secret"
cifail resolve 1 --note "bumped the package version" \
  --server http://your-host:8080 --server-token "a-long-random-secret"
# Auto-resolve failures your repo has since moved past, on the shared history:
cifail reconcile --server http://your-host:8080 --server-token "a-long-random-secret"
```

`analyze --server` posts the log to the server's `POST /analyze`, so the whole team's runs land
in one shared history (and fire any configured notifications) — the result renders identically to
a local analyze. Reconciliation runs on your machine (it reads your git history) and writes the
results back to the shared server, so a central service needs no checkout of its own.

**Web dashboard.** Open the server's root (`http://your-host:8080/`) in a browser for a small
built-in dashboard: a trends strip at the top (totals, recurrence rate, mean time to resolve,
top recurring and flaky failures), then browse recent failures, filter by ecosystem/status/repo,
read the details and resolution status (`✓` manual, `✓ auto`), and mark a failure resolved.
It's rendered server-side (no separate install, nothing to build). When the server requires a
token, browsers sign in once at `/login` — the token is checked and an HttpOnly session cookie
(`cifail_auth`, SameSite=Strict, Secure over https) protects the dashboard from then on; the
token never lives in page JavaScript or browser storage.

**Authentication.** Set `CIFAIL_SERVER_TOKEN` (or `serve --token`) and the server requires
`Authorization: Bearer <token>` on every request except `/healthz` and the sign-in page.
Browsers get redirected to `/login` instead of a bare 401; API clients send the token via
`--server-token` or the same `CIFAIL_SERVER_TOKEN` env var. If you start `serve` without a
token it runs open (dashboard included) and logs a loud warning — only acceptable on a
trusted network.

**Per-client tokens (rotate/revoke individually).** Instead of one shared secret you can issue a
token per client so any one can be revoked without disturbing the others. Provide a comma list in
`CIFAIL_SERVER_TOKENS`, and/or a file via `serve --tokens-file <path>` (one `<token> [name]` per
line; `#` comments allowed). Every configured token — including a single `--token` — is accepted
and compared in constant time; revoke a client by removing its entry and restarting.

**Mutual TLS (optional).** For a zero-trust setup, require a client certificate on top of (or
instead of) the token: `serve --client-ca <ca.pem> --tls-cert <server.pfx> [--tls-password <pw>]`.
The server then terminates HTTPS with `server.pfx` and rejects, at the TLS handshake, any client
whose certificate doesn't chain to `ca.pem`. A client CA without a server cert is a startup error
(mTLS needs server TLS). The Helm chart can mount both from a Secret (see `deploy/README.md`).

There's a Helm chart for Kubernetes in [`deploy/`](./deploy); it sources the token from a
Secret (`auth.existingSecret`) and injects it as `CIFAIL_SERVER_TOKEN`.

**Similarity at scale (optional).** By default "similar past failures" is computed in-process
with TF-IDF, which is perfect locally but loads recent history on every call. For a large shared
history you can switch to vector search: use the `pgvector` database provider (PostgreSQL + the
`vector` extension) and turn on embeddings (`CIFAIL_AI_EMBEDDINGS=1`). cifail then asks the
configured AI provider (local Ollama by default, e.g. `ollama pull nomic-embed-text`) for an
embedding per failure and lets the database do nearest-neighbour search. Everything stays
**off and TF-IDF by default** — this is purely opt-in. The embedding size must match the column:
set `CIFAIL_AI_EMBED_DIM` if your model isn't 768-wide.

**Notifications (optional).** The shared server can alert a chat channel or a webhook when a
failure appears, when a known one comes back, or when one is resolved — so the CLI stays quiet
and offline while the central service does the talking. Add a `notifications:` block to the
server's config:

```yaml
notifications:
  events: [new-failure, recurrence, resolved]   # omit or leave empty for all three
  slackWebhookUrl: https://hooks.slack.com/services/...   # or set CIFAIL_NOTIFY_SLACK_URL
  webhookUrl: https://example.com/hook                    # generic JSON POST; or CIFAIL_NOTIFY_WEBHOOK_URL
  discordWebhookUrl: https://discord.com/api/webhooks/... # or set CIFAIL_NOTIFY_DISCORD_URL
  teamsWebhookUrl: https://outlook.office.com/webhook/...  # or set CIFAIL_NOTIFY_TEAMS_URL
  dedupeSeconds: 300        # suppress repeats of the same (event, failure) within this window
  smtp:                     # optional email channel
    host: smtp.example.com
    from: cifail@example.com
    to: team@example.com
    # password is read from the env var named by passwordEnv (default CIFAIL_SMTP_PASSWORD)
  github:                   # optional: open/comment a GitHub issue per distinct failure
    repo: owner/name
    tokenEnv: GITHUB_TOKEN  # token is read from this env var, never the file
    labels: [cifail]
```

Secrets stay out of the file: the Slack/webhook/Discord/Teams URLs can come from the matching
`CIFAIL_NOTIFY_*_URL` env vars, and the SMTP password and GitHub token always come from env vars.
The **GitHub-issue** channel is idempotent — it opens one issue per distinct failure (tagged with a
hidden fingerprint marker) and comments on it when the failure recurs, rather than opening duplicates.
Notifications are **off unless a channel is configured**, fire only server-side, and are best-effort —
a broken channel is logged-and-swallowed and never affects analysis.

---

## Contributing

cifail is built to be extended, and the most valuable contribution is teaching it a new
failure pattern — that's plain YAML, no C# required. The
[**CONTRIBUTING guide**](./CONTRIBUTING.md) walks through adding a rule, building from
source, and the project's design principles.

Spotted a failure cifail *should* recognize but doesn't? Open an issue with the log (or a
redacted snippet) and we'll add a pattern for it.

---

## Project status

🚀 **Usable today.** Offline analysis across a dozen ecosystems, history that remembers your
fixes, optional AI, a shared team server, and CI integration all work now.

What's in the box:

- **Offline failure analysis** for .NET, Node, Python, Java, Go, Rust, Ruby, PHP, C/C++,
  Swift, Android, and infra (Docker/Terraform), plus generic CI errors — human-readable or `--json`.
- **Memory** — past failures and your fixes, with similar-failure recall and git-linked
  auto-resolution.
- **One-command install** as a native binary on macOS / Linux / Windows — no .NET needed.
- **Insights** — `cifail stats` and a web dashboard: recurrence, flaky failures,
  mean-time-to-resolution, and a by-ecosystem breakdown. `cifail stats --tests` ranks the
  noisiest tests, and `cifail clusters` groups near-duplicate failures into distinct root causes.
- **Structured test reports** — `analyze --format junit|trx`, with GitHub Actions annotations.
- **CI integration** — a GitHub Action and a GitLab component that explain failures and can
  comment on the PR / MR, plus `analyze --report sarif|markdown` for GitHub Code Scanning
  or a step summary.
- **Optional AI** — local [Ollama](https://ollama.com) by default (or hosted Anthropic/OpenAI)
  when the rules are unsure, plus AI-assisted rule drafting with `cifail suggest-rule`.
- **Team-ready (optional)** — shared PostgreSQL / MySQL / SQL Server / MongoDB, a `cifail serve`
  HTTP API secured by bearer token or mTLS with a built-in dashboard, vector similarity via
  pgvector, and Slack / Discord / Teams / webhook / email / GitHub-issue notifications.

## License

[MIT](./LICENSE) — free to use, change, and share.
