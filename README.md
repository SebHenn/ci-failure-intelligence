# cifail — CI Failure Intelligence

> Local-first developer intelligence for CI/build/test logs. Tells you **what broke,
> whether it has happened before, and how to fix it** — offline, language-agnostic,
> open source. AI is an optional add-on, never a requirement.

`cifail` takes a build/test log (from a file or stdin), matches it against a library of
failure rules, finds similar past failures you've already seen, and prints a clear root
cause + suggested fix.

```console
$ dotnet build 2>&1 | cifail analyze
┌─ Root cause ─────────────────────────────────────────────┐
│ NuGet package not found  (dotnet · dependency · 0.90)    │
│ Package 'Newtonsoft.Jsn' was not found in the configured │
│ sources. Check the name/version, or add the right NuGet  │
│ source in nuget.config.                                  │
└──────────────────────────────────────────────────────────┘
```

## Status

🚧 Early development. Building toward the MVP described in the milestones below.

## Why

CI logs are long, noisy, and repetitive. The signal — the one line that actually
explains the failure — is buried, and the fix is often something you (or a teammate)
already figured out last month. `cifail` is built around three ideas:

- **Local-first & offline** — your logs never leave your machine. No account, no cloud.
- **Rules before AI** — deterministic pattern matching handles the common cases
  instantly. A local LLM (via [Ollama](https://ollama.com)) is an *optional* fallback
  for the unknown ones.
- **Memory** — it remembers past failures and the fixes you recorded, so recurring
  problems get faster to resolve over time.

## Install

> Not yet published. For now, run from source:

```console
git clone https://github.com/SebHenn/ci-failure-intelligence.git
cd ci-failure-intelligence
dotnet run --project src/CiFail.Cli -- analyze samples/nuget-nu1101.log
```

Once released it will ship as a .NET global tool:

```console
dotnet tool install --global cifail
```

## Usage

```console
cifail analyze <path...>      # analyze one or more log files (reads stdin if no path)
cifail analyze --json x.log   # machine-readable output for CI pipelines
cifail analyze --ai x.log     # also consult a local Ollama model on low confidence
cifail history                # browse past analyses
cifail resolve <id> --note    # record how a failure was fixed
cifail rules list             # inspect loaded rule packs
```

## Data & configuration

History and user rule packs live under `~/.cifail/` (`history.db`, `rules/`). Set the
`CIFAIL_HOME` environment variable to relocate this directory — handy for CI, tests,
or keeping projects isolated.

## How it works

```
ingest → normalize (strip ANSI/timestamps) → detect ecosystem → rule match
       → root cause + fix → similarity vs. past failures → persist → render
```

See [`docs`](./docs) / the plan for the full architecture.

## Roadmap

- **M0** — Scaffolding, CI, license. ✅
- **M1** — Offline analyze: ingest, .NET rule pack, rule engine, console + `--json`. ✅
- **M2** — Memory: SQLite history + TF-IDF similarity + `history`/`resolve`. ✅
- **M3** — Breadth: Node, Python, Generic CI rule packs.
- **M4** — Optional AI via Ollama, gated behind `--ai`.
- **M5** — Release: docs, samples, `dotnet tool` packaging, NuGet + GitHub Releases.

## Contributing

Rule packs are plain YAML — adding a new failure pattern is the easiest way to help.
See [CONTRIBUTING.md](./CONTRIBUTING.md) (coming soon).

## License

[MIT](./LICENSE)
