# Contributing to cifail

Thanks for helping make CI failures easier to understand. The highest-leverage
contribution is **teaching cifail a new failure pattern** — and that's pure data, no C#.

## Project principles (please keep these)

- **Offline-first.** The core analysis never needs the network. Anything that reaches out
  (AI, notifications, a remote server) is opt-in and degrades gracefully when unavailable.
- **Small by default.** The shipped single-file binaries are CLI + SQLite only. External DB
  providers and the `serve` HTTP API are size-gated into the full/Docker build
  (`-p:IncludeExternalDb=true`). Don't add heavy dependencies to `CiFail.Core` or
  `CiFail.Cli`.
- **Rules are data.** Prefer a YAML rule over code. Most coverage needs no C# at all.
- **Two layers.** All logic lives in `CiFail.Core` (no console dependencies); `CiFail.Cli`
  is thin Spectre commands that call Core.
- **FluentAssertions is pinned to 7.2.0** (MIT). v8+ is commercially licensed — do not
  upgrade it.
- The project targets **net8.0** (it builds fine with the .NET 9 SDK).

## Add a failure pattern (the common case)

A rule is one entry in a YAML pack under
[`src/CiFail.Core/rulepacks`](./src/CiFail.Core/rulepacks). Packs are embedded resources, so
adding a rule to an existing file is enough — no project changes.

### 1. Write the rule

```yaml
- id: my-tool-widget-error          # stable, unique, kebab-case
  ecosystem: generic                # dotnet|node|python|java|go|rust|ruby|generic
  category: dependency              # loose grouping: dependency|compile|build|test|environment|network|auth|ci
  title: Widget registry rejected the request
  match: "WIDGET-(?<code>\\d+): (?<message>.+)"   # regex; named groups become {placeholders}
  confidence: 0.85                  # 0..1 — see guidance below
  fix: |
    The widget registry rejected the build (code {code}): {message}
    Check your WIDGET_TOKEN secret is set and not expired, then re-run.
  docs: https://example.com/widget/errors   # optional
```

Notes:

- The regex runs against the **normalized** log (ANSI stripped, CI timestamps removed) with
  `IgnoreCase | Multiline`. Use `cifail rules test` (below) to iterate.
- **Named capture groups** (`(?<name>...)`) are interpolated into `fix` (and `title`) via
  `{name}`. Scrub volatile tokens out of captures where you can.
- **Confidence guidance:** `0.9+` an unambiguous, well-known error code; `0.6–0.85` a
  strong signal; `0.4–0.55` a generic/ambiguous signal. Generic rules sit lower so an
  ecosystem-specific rule wins when both match.
- New ecosystem? Add an enum value to `Models/Ecosystem.cs`, marker regexes to
  `Ingest/EcosystemDetector.cs`, and a new `rulepacks/<eco>.yaml`. That's the only case
  that touches C#.

### 2. Test it as you write

```bash
# Try the regex against a real log before wiring it in:
cifail rules test "WIDGET-(?<code>\\d+): (?<message>.+)" --file widget-fail.log

# Inspect a loaded rule:
cifail rules explain my-tool-widget-error

# Lint every pack (this is what CI runs):
cifail rules validate src/CiFail.Core/rulepacks
```

### 3. Add a fixture + a test case

1. Drop a representative log at
   `tests/CiFail.Core.Tests/fixtures/<name>.log` (committed via the `!fixtures` un-ignore
   rule in `.gitignore`).
2. Add a row to
   [`RulePackBreadthTests`](./tests/CiFail.Core.Tests/Rules/RulePackBreadthTests.cs) —
   usually one `[InlineData(...)]` asserting the ecosystem + expected rule id.
3. For a headline new ecosystem, also add a `samples/<name>.log` for docs/demos.

### 4. Run the suite

```bash
dotnet build
dotnet test
```

`dotnet test` runs the Core, Server, and Providers suites. The external-DB integration
tests are skipped unless `CIFAIL_DB_IT=1` (they need Docker).

## Submitting

- Keep PRs focused; one feature/pattern set per PR.
- Make sure `dotnet test` is green and `cifail rules validate src/CiFail.Core/rulepacks`
  exits 0.
- Match the surrounding code style and the plain, beginner-oriented tone of the output
  ("What broke" / "How to fix it", confidence shown as high/medium/low).

Thank you! 🛠️
