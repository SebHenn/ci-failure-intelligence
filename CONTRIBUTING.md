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

## Build from source

cifail is written in C# (.NET 8). You'll need the
[.NET SDK](https://dotnet.microsoft.com/download) (8 or newer):

```console
git clone https://github.com/SebHenn/ci-failure-intelligence.git
cd ci-failure-intelligence
dotnet build                                          # build the solution
dotnet test                                           # run the tests
dotnet run --project src/CiFail.Cli -- analyze samples/nuget-nu1101.log
bash scripts/publish.sh linux-x64                     # build a standalone binary
```

While running from source, `dotnet run --project src/CiFail.Cli --` stands in for the
installed `cifail` command. The `cifail rules` commands that help while authoring a rule are
covered in [Test it as you write](#2-test-it-as-you-write) below.

See [CLAUDE.md](./CLAUDE.md) for the full command reference and architecture notes.

### If you use an AI coding agent

`CLAUDE.md` is the architecture brief, and `.claude/settings.json` is a committed permission
allowlist covering the commands this repo actually runs — `dotnet build`/`test`, the version
and rule-pack linters, and read-only `gh` queries. Merging and force-pushing are deliberately
left as prompts rather than grants.

It's checked in because a permission allowlist is a security boundary, and a reviewable one in
the repo beats an invisible one on each contributor's machine. Personal overrides belong in
`.claude/settings.local.json`, which is gitignored. Nothing here is required to contribute —
delete it locally and everything still builds.

One repo-specific trap it does **not** paper over: running the CLI by hand writes history to
your real `~/.cifail/`. Set `CIFAIL_HOME` to a temp directory first (see [CLAUDE.md](./CLAUDE.md)).

## Add a failure pattern (the common case)

A rule is one entry in a YAML pack under
[`src/CiFail.Core/rulepacks`](./src/CiFail.Core/rulepacks). Packs are embedded resources, so
adding a rule to an existing file is enough — no project changes.

> **Tip — let cifail draft it for you.** For a log that nothing matches yet, `cifail suggest-rule
> <log>` asks a local AI to draft a rule and validates it (it must compile, actually match the log,
> and not be overbroad) before showing it. Use the preview as a starting point, refine it by hand,
> then move the final rule into the appropriate pack below. Needs a local model (Ollama); it
> degrades to a friendly message when none is configured.

### 1. Write the rule

```yaml
- id: my-tool-widget-error          # stable, unique, kebab-case
  ecosystem: generic                # dotnet|node|python|java|go|rust|ruby|php|cpp|infra|swift|android|scala|elixir|generic
  category: dependency              # loose grouping: dependency|compile|build|test|environment|network|auth|ci
  title: Widget registry rejected the request
  match: "WIDGET-(?<code>\\d+): (?<message>.+)"   # regex; named groups become {placeholders}
  confidence: 0.85                  # 0..1 — how *sure*; see guidance below
  fix: |
    The widget registry rejected the build (code {code}): {message}
    Check your WIDGET_TOKEN secret is set and not expired, then re-run.
  docs: https://example.com/widget/errors   # the tool's own reference page for this error
```

All the optional fields, none of which existed before v0.3 — leave them out and the rule
behaves exactly as it always did:

```yaml
  severity: error         # error|warning|note — how *bad*, as opposed to how sure.
                          # Drives the SARIF level. Omitted => derived from confidence.
  requires: "Running tests"   # regex that must ALSO appear somewhere in the log
  notMatch: "known flake"     # regex that SUPPRESSES this rule when it also matches
  ecosystems: [go]        # extra ecosystems beyond `ecosystem:`, for genuinely cross-cutting rules
  enabled: false          # switch a rule off (see "Turning off a shipped rule" below)
```

Notes:

- The regex runs against the **normalized** log (ANSI stripped, CI timestamps removed) with
  `IgnoreCase | Multiline`. Use `cifail rules test` (below) to iterate.
  **`Multiline` is not `Singleline`:** `.` does not cross a newline. A shipped rule was dead
  for exactly this reason.
- **Named capture groups** (`(?<name>...)`) are interpolated into `fix` (and `title`) via
  `{name}`. Scrub volatile tokens out of captures where you can.
- **Confidence guidance:** `0.9+` an unambiguous, well-known error code; `0.6–0.85` a
  strong signal; `0.4–0.55` a generic/ambiguous signal. Generic rules sit lower so an
  ecosystem-specific rule wins when both match.
  **If your rule can fire on the same line as an existing one, the more specific rule must
  have the strictly higher confidence** — equal confidence makes the winner arbitrary, which
  was live between two Go rules until fixtures exposed it.
- **Every `{placeholder}` in `fix` must be captured by *every* top-level alternation branch
  of `match`.** `cifail rules validate` fails the build otherwise. If your pattern is
  `A(?<file>…)|B`, a log matching `B` leaves `{file}` in the rendered text and the user is
  shown your template instead of an answer — eleven shipped rules did exactly this. Two ways
  to comply: give both branches the **same group name** (.NET allows duplicates, and that is
  the preferred fix), or write a fix that doesn't need the capture. Never use `name2`-style
  suffixes to dodge the duplicate. The matched line is displayed anyway, so "the line above
  names it" reads perfectly well.
- **`requires` / `notMatch` beat a clever regex.** Before these existed, "only when X is also
  present" and "except when Y" had to be crammed into the one pattern as lookarounds —
  `go-build-failed` still does, and it is unreadable. Both guards run under the same 2-second
  timeout as `match`, and a guard that can't be evaluated leaves the rule **quiet** rather than
  firing anyway (failing open on a `notMatch` would let through exactly what it exists to stop).
- **`severity` is not `confidence`.** Confidence is how sure cifail is that this is what
  happened; severity is how bad it is. Only set it when the two genuinely differ — a
  confidently-identified deprecation notice (`severity: note`, `confidence: 0.9`) or a
  tentative but fatal OOM.
- **`docs:` is expected**, not decorative: it's where a reader goes when the `fix` text isn't
  enough. All 136 shipped rules have one and `cifail rules validate` warns when a rule doesn't.
- New ecosystem? Add an enum value to `Models/Ecosystem.cs`, marker regexes to
  `Ingest/EcosystemDetector.cs`, and a new `rulepacks/<eco>.yaml`. That's the only case
  that touches C#. Note `ecosystems:` is *not* the way to express "Android builds are JVM
  builds" — that's `RuleEngine.Inherits`, a property of the ecosystems rather than of any one
  rule.

### Turning off a shipped rule

Put a stub in one of your own rule packs (`~/.cifail/rules/`, your repo's `.cifail/rules/`,
or anything on `--rules`). A user rule wins over a shipped one with the same id:

```yaml
- id: generic-nonzero-exit
  enabled: false
```

That's the whole file — a disabled rule needs no `match`, `fix` or `docs`, and
`rules validate` won't ask for them.

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

**This step is not optional — the build fails for a rule with no fixture.** A rule is data,
so a rule nothing exercises is an untested claim: nothing proves the regex ever fires on what
the tool actually prints, and nothing notices when it stops.

1. Drop a representative log at
   `tests/CiFail.Core.Tests/fixtures/<name>.log` (committed via the `!fixtures` un-ignore
   rule in `.gitignore`).

   > **Copy real tool output. Do not write the fixture from your regex.** Three shipped rules
   > could never have matched anything real — a reversed shell word order, a `.` that had to
   > cross a newline, and a wording composer doesn't use — and all three passed review. Only a
   > realistic fixture caught them. Run the tool, break it on purpose, paste what it printed.

2. Add a row to the `EcosystemRules` (or `GenericRules`) `TheoryData` in
   [`RulePackBreadthTests`](./tests/CiFail.Core.Tests/Rules/RulePackBreadthTests.cs):
   `{ "<fixture>.log", Ecosystem.Node, "<rule-id>" }`. The assertion is that your rule
   **wins** the ranking, not merely that it matched — matching while losing to a vaguer rule
   is indistinguishable from not working.
3. For a headline new ecosystem, also add a `samples/<name>.log` for docs/demos.

### 4. Run the suite

```bash
dotnet build
dotnet test
```

`dotnet test` runs the Core, Cli, Server, and Providers suites. The external-DB integration
tests are skipped unless `CIFAIL_DB_IT=1` (they need Docker).

## Submitting

The [pull-request template](./.github/PULL_REQUEST_TEMPLATE.md) has the full checklist; the
short version:

- Keep PRs focused; one feature/pattern set per PR.
- Make sure `dotnet test` is green and `cifail rules validate src/CiFail.Core/rulepacks`
  exits 0.
- Match the surrounding code style and the plain, beginner-oriented tone of the output
  ("What broke" / "How to fix it", confidence shown as high/medium/low).
- **stdout is the answer, stderr is everything about the run.** Commands write through
  `CliConsole.Out` / `CliConsole.Err`, never `AnsiConsole.*` directly, and exit codes come
  from `Cli/ExitCodes.cs`.
- Anything user-visible gets an entry under `## [Unreleased]` in
  [`CHANGELOG.md`](./CHANGELOG.md).

### What the branch protection on `main` means for you

`main` takes changes through pull requests only, and it can't be force-pushed or deleted.
Three checks must pass before a PR can merge:

| Check | What it does | Typical |
|---|---|---|
| `build-test` | version consistency, `shellcheck`, packaging templates, build, rule-pack lint, full test suite, `dotnet pack` | ~1 min |
| `docker-smoke` | builds the full image **and runs it** (`--version`, `serve --help`, an analyze) | ~2 min |
| `db-integration` | the external-DB contract tests against real Postgres/MySQL/SQL Server/MongoDB | ~5 min |

**Your branch must also be up to date with `main` before it can merge.** If `main` moves
while your PR is open, GitHub will ask you to update the branch and re-run the checks. That
isn't ceremony: a PR that went green against a three-commit-old base has proven something
about a commit that will never exist. We shipped a bug that way once — a crash in
`cifail serve --help` reached `main` because nothing built the Docker image, and the PRs open
at the time were still reporting green against the base from before the fix.

No approving review is required, so a green PR is mergeable as soon as the checks finish. You
don't need write access to contribute — fork, branch, and open the PR from your fork.

## Cutting a release (maintainers)

Contributors never need this; it is here so the release settings are written down next to the
branch protection they sit beside.

1. Bump `<Version>` in `Directory.Build.props` (and the Helm chart's `appVersion`), close
   `## [Unreleased]` in `CHANGELOG.md`, and merge that to `main` with CI green.
2. Tag the merged commit **on `main`** and push the tag:

   ```console
   git tag v0.4.0
   git push origin v0.4.0
   ```

3. **Approve the deployment.** A few minutes in, the run shows *Waiting for review* on two jobs,
   `nuget-publish` and `docker`, both in the `release` environment. They wait on the same jobs, so
   they start waiting together: choose **Review deployments**, tick `release`, and **Approve and
   deploy** once — that releases both. Before approving, the `nuget-pack` job summary lists the
   SHA-256 of the package about to be pushed. The draft release and the install smoke test run
   without waiting for you, so if you would rather nothing reach nuget.org or GHCR before the
   macOS and Linux installs have passed, wait for `smoke` to go green and approve then.
   **Rejecting** ends the run with nothing published; delete the draft release it left behind.
4. `finalize` then publishes the release and moves the `v1` tag (not for a prerelease such as
   `v0.4.0-rc1`).

`release.yml` refuses a tag that does not match `<Version>`, or whose commit is not on `main`,
before it builds anything. **Either failure spends the tag:** version tags cannot be moved or
deleted (see below), so the fix is a tag for a version that is not taken yet — bumping
`<Version>` to match if need be — never a second push of the same tag. Nothing was published, so
the skipped version costs only a gap in the numbering.

**Rehearse it** with `gh workflow run release.yml --ref main`. A manual run is always a dry run,
even when started from a tag: it builds every binary, renders the manifests, packs the NuGet
package and builds the multi-arch image, and publishes nothing. It needs no approval, because
the image build it runs (`docker-dryrun`) is a different job from the one that pushes.

### Release settings

| Setting | Value | Why |
|---|---|---|
| Environment `release` | required reviewer `SebHenn`; administrators cannot bypass; deployment tags `v*` only, no branches | The only jobs that publish outside this repository run in it, so a tag pushed by anything with write access does not publish on its own. With one maintainer the reviewer is usually whoever pushed the tag; the approval is still a deliberate pause between "a tag exists" and "nuget.org and GHCR have it". |
| nuget.org trusted publishing policy | owner `SebHenn`, repository `ci-failure-intelligence`, workflow file `release.yml`, environment `release` | The environment is what ties the publishing key to an approved job. A policy with Environment blank still works with this workflow but accepts a token from any job in it, so set it. Renaming the workflow file or the environment breaks publishing. |
| Tag ruleset `release tags` | `refs/tags/v*.*.*`: no update, no deletion, no force push, no bypass | A released version tag can never point at different code. The pattern must **not** match `v1`, which `finalize` force-moves on every release — `v*` would, and the move would fail. |
| Immutable releases | on | Once `finalize` publishes a release, its assets and its tag cannot change. That is why it stays a draft until everything has passed. |
| Workflow permissions | read-only by default | Every job in `release.yml` declares its own; `ci.yml` declares `contents: read`. |
| Actions pinning | every `uses:` is a full commit SHA with a `# vX.Y.Z` comment | A tag is a pointer its owner can move. Dependabot updates the SHA and the comment together. |

Thank you! 🛠️
