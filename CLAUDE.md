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

# Package as a global tool:
dotnet pack src/CiFail.Cli/CiFail.Cli.csproj -c Release -o ./nupkg

# Build self-contained, single-file native binaries (no .NET needed to run them):
bash scripts/publish.sh                # all 6 RIDs -> dist/
bash scripts/publish.sh linux-x64      # one RID
dotnet publish src/CiFail.Cli/CiFail.Cli.csproj -c Release -r win-x64 -o dist/win-x64
# Releases are cut by pushing a tag (git tag v0.1.0 && git push origin v0.1.0),
# which triggers .github/workflows/release.yml to build the matrix + attach binaries.
```

The published executable is named **`cifail`** (set via `<AssemblyName>`), not
`CiFail.Cli`. Self-contained/single-file props in `CiFail.Cli.csproj` are gated on
`'$(RuntimeIdentifier)' != ''`, so they only apply during `dotnet publish -r <rid>` and
never affect plain build/test/pack.

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
- **`tests/CiFail.Core.Tests`** — xUnit + FluentAssertions; fixtures in `fixtures/*.log`.

### The analyze pipeline (`Core/Analysis/AnalysisService.cs`)

`Analyze()` is the orchestrator and the single entry point for the whole flow:

1. `Ingest/LogNormalizer.Build` — strip ANSI, normalize newlines, drop leading CI
   timestamps. Also exposes `Scrub()`, which collapses volatile tokens (paths,
   numbers, GUIDs) — used for fingerprints and similarity so cosmetic differences
   don't matter.
2. `Ingest/EcosystemDetector.Detect` — marker-count heuristic (dotnet/node/python),
   falls back to `generic`; overridable via `--type`.
3. `Rules/RuleEngine.Match` — runs applicable rules, returns matches ranked by
   confidence. Rules whose `ecosystem` is `generic` always apply; ecosystem-specific
   rules only apply when detected (or when ecosystem is undetected). Named regex
   capture groups are interpolated into the rule's `fix` template via `{name}`.
4. `Analysis/FingerprintBuilder` — `ruleId:hash` identity from scrubbed signature.
5. Similarity + persistence (only when an `IAnalysisStore` is supplied): TF-IDF cosine
   over scrubbed bag-of-terms vs. stored history; then persists the run unless
   `--no-history`.

`AnalysisService.CreateDefault()` = rules only, no store (used by tests and the
offline path). `CreateWithStore(store)` = adds similarity + history. The store is
injected via the `IAnalysisStore` interface so the pipeline never hard-depends on
SQLite and tests can use a temp-file repository.

### Rule packs (`Core/rulepacks/*.yaml`)

Rules are **data, not code** — this is the primary extension point. Each pack is a
YAML list of `{ id, ecosystem, category, title, match (regex), confidence, fix, docs }`.
Packs are **embedded resources** (see `EmbeddedResource` glob in `CiFail.Core.csproj`),
loaded by `RulePackLoader` from the assembly; users can add `~/.cifail/rules/*.yaml`,
and a user rule with a duplicate `id` overrides the embedded one. A malformed regex is
skipped, never fatal. To add coverage for a new failure, add a rule + a fixture +
a test case in `RulePackBreadthTests` — usually no C# changes needed.

### Storage (`Core/Storage/`)

`SqliteAnalysisRepository` (Microsoft.Data.Sqlite) creates its schema on first use.
Term vectors are stored as JSON per row and reloaded as the similarity corpus. Uses
`Pooling=False` so the db file handle releases promptly on dispose (otherwise the file
stays locked, which breaks temp-file test cleanup). All paths resolve through
`CiFailPaths`, which honors `CIFAIL_HOME`.

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
