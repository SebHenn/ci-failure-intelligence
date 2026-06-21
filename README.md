# cifail — understand why your build failed

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

## What problem does this solve?

A failing CI log can be hundreds of lines long, and the line that matters is buried
somewhere in the middle. Figuring out *which* line, and what to do about it, takes
experience. cifail does that first pass for you:

- It knows the common failure patterns for **.NET, Node/npm, Python/pip**, and generic
  CI errors, so it can point straight at the cause.
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

**Homebrew** (macOS / Linux):

```console
brew install SebHenn/tap/cifail
```

**Windows** ([Scoop](https://scoop.sh)):

```console
scoop install https://raw.githubusercontent.com/SebHenn/ci-failure-intelligence/main/packaging/scoop/cifail.json
```

**Manual:** grab the binary for your OS from the
[Releases page](https://github.com/SebHenn/ci-failure-intelligence/releases), unzip it,
and put it on your `PATH`.

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
- `--type dotnet|node|python|generic` — tell cifail what kind of log this is, if it
  guesses wrong.
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
version instead of `:latest`), `summary` (write to the step summary, default `true`), and
`fail` (propagate cifail's exit code; off by default since it runs after a failed build).
For shared history, set `CIFAIL_DB_PROVIDER` / `CIFAIL_DB_CONNECTION` in the job env.

### GitLab CI

Include the template and point it at your log; it runs only `on_failure` and never fails
the pipeline itself:

```yaml
include:
  - remote: 'https://raw.githubusercontent.com/SebHenn/ci-failure-intelligence/main/ci-templates/gitlab.yml'

build:
  stage: build
  script:
    - your-build-command 2>&1 | tee build.log

explain-failures:
  extends: .cifail
  needs: ["build"]
  variables:
    CIFAIL_LOG: build.log
```

### Anywhere else (plain pipe)

If you've installed the binary (or are in a `cifail` container), just pipe into it:

```yaml
- name: Explain failures
  if: failure()
  run: cifail analyze build.log
```

---

## What do the words mean?

cifail tries to keep things plain, but a few terms show up:

- **ecosystem** — the kind of project the log came from: `dotnet`, `node`, `python`, or
  `generic` (anything else). cifail guesses this automatically.
- **confidence** — how sure cifail is about the cause: **high**, **medium**, or **low**.
  Low confidence means "this is my best guess — double-check it".
- **rule / pattern** — a single known failure cifail can recognize (for example, "NuGet
  package not found"). The set of all of them is what `cifail rules list` shows.

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
cifail history --server http://your-host:8080 --server-token "a-long-random-secret"
cifail resolve 1 --note "bumped the package version" \
  --server http://your-host:8080 --server-token "a-long-random-secret"
# Auto-resolve failures your repo has since moved past, on the shared history:
cifail reconcile --server http://your-host:8080 --server-token "a-long-random-secret"
```

Reconciliation runs on your machine (it reads your git history) and writes the results back to
the shared server, so a central service needs no checkout of its own.

**Web dashboard.** Open the server's root (`http://your-host:8080/`) in a browser for a small
built-in dashboard: browse recent failures, filter by ecosystem/status/repo, read the details
and resolution status (`✓` manual, `✓ auto`), and mark a failure resolved. It's a single
bundled page (no separate install) that calls the same JSON API; if the server requires a
token, paste it into the field at the top once and it's remembered in your browser.

**Authentication.** Set `CIFAIL_SERVER_TOKEN` (or `serve --token`) and the server requires
`Authorization: Bearer <token>` on every request except `/healthz` and the dashboard shell.
Clients send it via `--server-token` or the same `CIFAIL_SERVER_TOKEN` env var. If you start
`serve` without a token it runs open and logs a loud warning — only acceptable on a trusted
network. (mTLS is a possible future hardening on top of the token.)

There's a Helm chart for Kubernetes in [`deploy/`](./deploy); it sources the token from a
Secret (`auth.existingSecret`) and injects it as `CIFAIL_SERVER_TOKEN`.

---

## Want to help? Add a pattern

The easiest way to contribute is to teach cifail a new failure. Patterns are written in
plain YAML files (no C# needed) — see the existing ones in
[`src/CiFail.Core/rulepacks`](./src/CiFail.Core/rulepacks). A full `CONTRIBUTING.md` is
coming soon.

### Build from source

cifail is written in C# (.NET 8). If you want to hack on it you'll need the
[.NET SDK](https://dotnet.microsoft.com/download) (8 or newer):

```console
git clone https://github.com/SebHenn/ci-failure-intelligence.git
cd ci-failure-intelligence
dotnet test                                           # run the tests
dotnet run --project src/CiFail.Cli -- analyze samples/nuget-nu1101.log
bash scripts/publish.sh linux-x64                     # build a standalone binary
```

*(While running from source, `dotnet run --project src/CiFail.Cli -- ` stands in for the
installed `cifail` command.)* See [CLAUDE.md](./CLAUDE.md) for architecture notes.

---

## Project status & roadmap

🚧 Early but usable. Offline analysis, memory, and the .NET/Node/Python/Generic patterns
all work today.

Done:
- **Explain failures offline** (.NET, Node, Python, generic patterns), human or `--json`. ✅
- **Remember** past failures and your fixes, and surface similar ones. ✅

Next (see the [plan](https://github.com/SebHenn/ci-failure-intelligence)):
- **One-command install** as a native binary on any OS — no .NET needed. ✅
- **Pick your database** — keep the built-in SQLite, or point cifail at a shared
  PostgreSQL / MySQL / SQL Server / MongoDB. ✅
- **Resolutions that record themselves** — cifail links a failure to the commit that
  fixed it, so you don't have to run `resolve` by hand. ✅
- **Docker image** (full build, all databases) published to GHCR. ✅
- **GitHub Action & GitLab template** so a pipeline can explain its own failures. ✅
- **Shared team service** — `cifail serve` HTTP API (in the full/Docker build) + a Helm
  chart in [`deploy/`](./deploy), protected by a bearer token, with a built-in web
  dashboard at the server root. ✅
- **Optional AI** suggestions when the rules are unsure (`--ai`) — local [Ollama](https://ollama.com)
  by default, or hosted Anthropic/OpenAI (opt-in). ✅

## License

[MIT](./LICENSE) — free to use, change, and share.
