# Changelog

All notable changes to cifail are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and cifail
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
below `1.0.0`, a **minor** bump may carry the breaking changes that a major bump would
after 1.0.

## [Unreleased]

### Fixed

- **`analyze` now honours every `--report` / `--report-out` pair instead of only the last one.**
  Both options repeat and are paired by position, so one analysis can produce a markdown report
  for humans *and* a SARIF for Code Scanning. Previously the second pair silently replaced the
  first: exit 0, empty stderr, and a file the caller explicitly asked for that was never written.
  Combinations that cannot be paired unambiguously (more reports than destinations, a
  `--report-out` with no `--report`, two reports aimed at the same file) are now usage errors
  (exit 2) rather than a quiet guess. A single `--report` with no destination still takes over
  stdout, as before.

- **The GitHub Action no longer dies silently on the first non-zero command.** The runner invokes
  a composite step as `bash --noprofile --norc -e -o pipefail`, and the script's `set -uo pipefail`
  does not clear that inherited `errexit` — so `[ -n "$CIFAIL_DB_PROVIDER" ] && ...` with no
  shared database configured (and cifail's own documented exit 1 for "nothing matched") ended the
  step instantly, before `code=$?`, before cifail's stderr was echoed, and before the `fail:`
  input could apply. `fail: false` was inoperative and the one step whose job is to explain a
  failure went red without explaining itself. The script now sets `+e` explicitly, uses `if`
  blocks rather than `&&` for its optional arguments, and warns when the markdown report is
  missing instead of quietly falling back to raw JSON.

## [0.3.0] - 2026-08-15

Two rounds of work in one release: `cifail gate`, server observability and the richer dashboard
that landed after 0.2.0, plus a correctness round that fixed a security problem in rule loading,
eleven rules that were showing you their own templates, and a dashboard that had been quietly
searching only the newest 200 records.

**Breaking:** `analyze --json` is now always an array — see *Changed*.

### Added

- **`analyze --report gitlab`** writes a GitLab **Code Quality** report, so findings appear in
  the merge-request widget. GitLab ingests SARIF only from its own security scanners, so the
  SARIF cifail already produced was invisible there. GitLab dedupes across pipelines on the
  issue fingerprint, which is exactly what cifail's fingerprint already is. The CI/CD component
  emits it automatically as `artifacts:reports:codequality`.

- **`cifail rules list --json` and `rules explain --json`.** The rule inventory wasn't
  scriptable, so nothing could enumerate what a given cifail build actually knows. The listing
  carries the resolved search path alongside the rules — "which rules do I have" and "where did
  they come from" are the same question once a repository can ship its own packs.

- **`analyze` and `gate` now accept a directory, a glob, or a gzipped log.** `cifail analyze
  logs/` used to report `file not found: logs/`, which is actively misleading, and a glob only
  worked when the shell had already expanded it — so the README's own
  `cifail gate --format trx TestResults/*.trx` failed on PowerShell and cmd, on a project whose
  author develops on Windows. A directory is walked for `.log/.txt/.out/.xml/.trx/.gz`, an
  unexpanded glob is matched by cifail itself, `.gz` is decompressed (which is what CI providers
  hand you when you download a job log), and `-` reads stdin.

- **`cifail history` gained `--json` and filters** — `--since`, `--repo`, `--ecosystem`,
  `--rule`, `--open`, `--resolved`, `--search`, `--offset`. It was the one data-bearing command
  with no machine-readable output and no way to narrow the list, even though `stats` and
  `clusters` already accepted `--since`/`--repo` and every stored record is fully structured.
  `cifail history <id> --json` emits the single record.

- **`--no-color`, `--quiet` and `--verbose`, and `NO_COLOR` is honoured.** There was no way to
  turn colour off short of redirecting stdout, which also loses the human view — Spectre.Console
  does not implement `NO_COLOR` and cifail did not either. `FORCE_COLOR` and `CLICOLOR=0` are
  accepted too, with `NO_COLOR` winning. `--quiet` silences hints but never errors or warnings;
  `--verbose` shows every secondary match with its rule id, and **why the ecosystem was chosen**
  — `EcosystemDetector.Rank` was public and documented as the answer to "why did it think this
  was Java?" while nothing but its own tests ever called it. Detection being wrong is a common
  cause of "no rule matched", because the wrong ecosystem *narrows* the rule set.

- **The GitHub Action gained outputs** (`matched`, `rule`, `fingerprint`, `title`, `count`,
  `new-failures`, `exit-code`), so a later step can branch on the result instead of re-parsing
  text, and a **`mode: gate`** input for failing a PR on new failures only.

- **Documented snippets for Azure DevOps, Jenkins, Bitbucket Pipelines and CircleCI.** There was
  nothing at all for any of them.

- **CI now runs the build and tests on Windows as well as Linux.** Path handling, console
  encoding, the ASCII glyph fallback, SQLite file locking and the new glob expansion are all
  platform-sensitive, and cross-platform breakage could previously only surface in the release
  workflow — i.e. after a tag was already cut.

- **`cifail prune --older-than <duration>`** deletes old analyses. History had no delete path of
  any kind — no store method, no command, no retention setting — so `history.db` grew for the
  life of the install, holding a log excerpt and a term bag per failure. That is both unbounded
  disk and an ever-growing pile of log text at rest, which can contain secrets from the logs you
  analyzed (see SECURITY.md). Resolved failures only by default (an old failure nobody resolved
  is usually the one you least want to forget); `--include-open` takes those too, and
  `--dry-run` counts without deleting. Durations are `30d`, `6w`, `3mo`, `1y` — a bare `m` is
  rejected rather than guessed at, and an unparseable age is an error rather than "delete
  everything".

- **The dashboard gained free-text search and paging**, and the ecosystem dropdown is now built
  from the full history rather than one page of it.

- **Rules can say more about themselves.** Five optional fields, all of which a pack written
  before them behaves identically without:
  - `severity: error|warning|note` — how *bad* a failure is, as opposed to `confidence`'s how
    *sure*. One field was doing both jobs, so a confidently-identified deprecation notice and a
    tentatively-identified OOM came out the same way. It drives the SARIF level; rules without
    one still derive it from confidence.
  - `requires` — a second pattern that must also appear in the log for the rule to fire.
  - `notMatch` — a pattern that suppresses the rule when it also matches. Expressing "except
    when" previously meant a negative lookahead inside the one regex (`go-build-failed` still
    has one, and it is unreadable).
  - `ecosystems` — a list, for the genuinely cross-cutting rule, where the alternatives were
    duplicating it under a second id or demoting it to `generic` so it fires on everything.
  - `enabled: false` — switches a rule off. The point is silencing a *shipped* rule: a
    two-line stub with the same id is now enough, where before the only route was overriding it
    with a pattern that matches nothing, because `confidence: 0` is rejected.

  Guards run only after `match` hits, under the same 2-second timeout, and a guard that cannot
  be evaluated leaves the rule **quiet** — failing open on a `notMatch` would admit exactly the
  match it exists to suppress. `rules explain` shows all of it.

- **Failures now come with their surrounding log.** cifail used to show you one line, truncated
  to 100 characters — so for a compiler error it threw away the `Type 'string' is not assignable
  to type 'number'` printed underneath, which is the part that tells you what to change. The
  report now shows the matched line with the lines either side, each numbered and the matched one
  marked. `--context <N>` sets the window (default 3; `--context 0` restores the old terse view).

- **The report tells you which line it matched, and how many times.** A rule that fired twelve
  times looked exactly like one that fired once, because the engine only ever reported the first
  occurrence.

- **The report shows the rule id and the fingerprint.** Both were previously reachable only
  through `--json`, which meant you could read an entire report and still not know what to pass
  to `cifail rules explain`, or what line `cifail gate`'s baseline would key on.

- **SARIF results point at the line the rule matched**, with the matched text as a `snippet`.
  Every result was previously pinned to line 1, so GitHub Code Scanning put every annotation at
  the top of the file regardless of where the failure was. The snippet matters because the
  artifact a SARIF result names is usually a CI log that no longer exists by the time anyone
  reads the finding.

- **Markdown reports include the context block** rather than the bare line — on a PR comment or a
  step summary there is no terminal to go and look at the original log in.

- `--json` gains `LineNumber`, `ContextBefore`, `ContextAfter` and `OccurrenceCount` on each
  match. All four are **omitted when they carry nothing**, so an existing consumer sees exactly
  the document it saw before.

- **Stored history keeps the context block**, so `cifail history <id>` and the dashboard detail
  pane can show a failure with its surroundings. Previously the excerpt was the matched line
  alone, which meant a recorded failure could never be re-read in context once the CI log was
  gone. The excerpt cap grew from 500 to 2000 characters (note this is more log text at rest —
  see SECURITY.md).

- **A repository can ship its own rule packs.** Rules are most useful when they are specific to
  one repo ("two runs of the same seed produced different bytes"), and until now the only place
  cifail looked was `~/.cifail/rules` — so shipping a pack with a repo meant pointing
  `CIFAIL_HOME` at the checkout, which also moves `history.db` and gives you a different
  history depending on which directory you run from. Packs committed to **`.cifail/rules/`**
  (beside `cifail gate`'s baseline) are now found automatically by walking up from the working
  directory, and three explicit routes stack on top: `rules: { paths: [...] }` in `config.yaml`,
  the `CIFAIL_RULES` environment variable (a `PATH`-style list), and `--rules <dir>` on
  `analyze`, `gate` and `rules list`. Later locations override earlier rules with the same id.
  `cifail config` and `cifail rules list` print the resolved search path with each directory's
  pack count, `rules validate` lints all of them, and a pack that cifail merely *found* can no
  longer break analysis — an unparseable one is skipped and named by `rules validate`. The
  GitHub Action gains a matching `rules:` input.

- **`cifail gate`** — fail CI on a *new* failure while tolerating the known backlog, the way
  a linter baseline does. `cifail gate --update <logs>` accepts everything currently failing
  into a committed `.cifail/baseline.txt`; after that `cifail gate <logs>` exits 1 only for a
  fingerprint that isn't in it. The baseline is one fingerprint per line with the rule title
  as a comment, so it reads in review and deleting a line re-arms the gate. `gate` opens no
  store, reads no git, and makes no network call — its entire memory is that file, so it
  gives the same verdict on a laptop and in a scratch container. `--json`, `--format` and
  `--type` work as they do on `analyze`.

- **`cifail serve` exposes `GET /metrics`** in Prometheus text exposition format, computed by
  the same `StatsService` behind `GET /stats` and `cifail stats` — so a Grafana board and the
  CLI cannot disagree about a number. Gauges rather than counters (these are aggregates
  recomputed per scrape, not monotonic process counters), and the per-fingerprint series is
  capped at 10 because a fingerprint label is unbounded. Authenticated like every other
  route; see `deploy/README.md` for the scrape config.

- **`GET /openapi.json`** — a static OpenAPI 3.0 description of the API, with a test asserting
  every documented route still answers.

- **The dashboard gained three panels**: a failures-per-day sparkline over the last 30 days
  (drawn including the quiet days — a chart built only from the days that had failures hides
  exactly the gaps you want to see), the noisiest tests from the per-test flakiness data, and
  cluster drill-down that expands to the failures in each group and links straight to them.
  All of it is still server-rendered with no JavaScript: the chart is inline SVG and the
  drill-down is `<details>`, so both work with scripting disabled.

- **Scala/sbt and Elixir/Mix are now recognized ecosystems**, with 6 rules each: Scala type
  mismatch (Scala 2 *and* 3 wording), unresolved symbol, sbt dependency resolution,
  conflicting cross-versions, ScalaTest; Elixir compile errors, undefined/private functions,
  Mix dependency drift, Hex package resolution, ExUnit, and Elixir/OTP version mismatch.

- **Kubernetes and Helm** join Docker and Terraform in the `infra` pack: `ImagePullBackOff`,
  `CrashLoopBackOff`, rollout timeouts, failed Helm releases and template render errors.

- **Kotlin** rules in the `java` pack: unresolved reference, type mismatch (including
  nullability), and the Gradle JVM-target mismatch between `compileJava` and `compileKotlin`.

- **45 new rules in total** (91 → 136), each with a fixture proving it matches real tool
  output and wins the ranking:
  - **node** (6 → 14): `npm ci` lockfile mismatch, `EBADENGINE`, `EINTEGRITY`, yarn
    `--frozen-lockfile`, pnpm `ERR_PNPM_OUTDATED_LOCKFILE`, TypeScript `TS####`, Vitest and
    Playwright failures.
  - **python** (6 → 12): wheel build failure, PEP 668 `externally-managed-environment`,
    `poetry.lock` drift, `AttributeError`, mypy and Ruff.
  - **dotnet** (7 → 12): `NETSDK1004` (restore never ran), `NU1301` (unreachable feed),
    `MSB4018`, and MSTest/NUnit assertions — previously only xUnit was recognized.
  - **swift** (5 → 10): **XCTest failures** (there was no Swift test rule at all), SwiftPM
    resolution, missing package product, no matching simulator destination, and CocoaPods
    sandbox drift.

### Changed

- **`analyze --json` is now always a JSON array**, one element per analysis unit. It previously
  emitted a bare object for a single input and an array for several, so the document's shape
  depended on how many files a glob happened to match — `cifail analyze *.log --json | jq '.[0]'`
  worked until the day one log was left. A clean report now yields `[]` rather than nothing.
  **This is a breaking change to the `--json` contract**; wrap a single-object consumer in
  `.[0]`. (Below 1.0 a minor bump may carry breaking changes, as stated above.)

- **`cifail serve` gained `GET /readyz`**, and the Helm chart's readiness probe now points at it.
  `/healthz` was wired to both probes but never touched the store, so a server that could not
  reach its database reported itself both alive *and* ready and kept taking traffic it could only
  fail. `/healthz` deliberately still answers without touching the store — restarting a healthy
  pod because the database blinked makes an outage worse. Both remain public; the kubelet has no
  token.

### Fixed

- **`cifail stats` no longer presents a capped scan as the whole picture.** Aggregation loads
  whole rows and counts them in memory, so past the scan limit (5000) the total, the recurrence
  rate and the mean time to resolution all silently described the newest rows only. The snapshot
  now reports `Truncated`, the CLI shows `1204+` rather than `1204`, and `--json` carries the
  flag. A count that is approximate is fine; one that looks exact and isn't is not.

- **The GitHub Action's step summary and PR comment no longer paste box-drawing characters
  inside a code fence.** It now renders `--report markdown`, which existed for exactly this and
  went unused. It also stopped folding stderr into stdout with `2>&1`, which had been discarding
  the stream separation the CLI works hard to maintain.

- **The dashboard's filters only ever searched the newest 200 records.** It fetched
  `GetRecent(200)` and filtered that in memory, so "show me resolved failures" could not find one
  recorded any earlier — the page said "No failures match" as though it had looked. Filtering,
  counting and paging now happen in the store. A new side-interface (`IHistoryQuery`, alongside
  `IAnalysisStats` and `IClusterer`) carries it, with an in-memory fallback for stores that don't
  implement it; both paths share one definition of what each filter means, and a test asserts
  they return identical rows.

- **A very large log could exhaust memory before analysis started.** Nothing capped the input,
  while a log document holds the raw text, the normalized text and a line array, and the
  similarity path scrubs the whole thing seven more times. Logs over ~8 MB now keep their head
  and their tail and drop the middle, marked with a visible `[... cifail: middle of the log
  omitted ...]`. Both ends are kept deliberately: in CI the failure is at the end, while the
  beginning carries the tool versions and command line the ecosystem detector keys on.

- **`cifail serve` no longer blocks a response on outbound notifications.** Channels are blocking
  HTTP/SMTP calls with a 10-second timeout each, dispatched inline — so six configured channels
  could add a minute to the `POST /analyze` that triggered them. Delivery now runs off the
  request thread; the event filter and dedupe still run synchronously, so suppression decisions
  keep their order.

- **SQLite history gained an index on `analyzed_at`** (every `--since` filter sorted and ranged
  on it with no index) and now runs in **WAL** mode with a busy timeout, so a dashboard render no
  longer serializes against a concurrent `/analyze`.

- **`php-call-undefined` and `php-fatal-uncaught` both matched the same line with confidence
  0.8**, so which one was reported as the root cause came down to alphabetical order by rule id.
  The specific rule (which names the undefined symbol) now outranks the general one. A test
  asserts no fixture's top two matches share a confidence, since that is the only place the
  problem is detectable — two rules sharing a confidence is fine until they both fire on the
  same log.

- **Rule patterns now run under a 2-second timeout.** cifail loads rule packs from the
  repository you are working in (`.cifail/rules/`, added below), so running `cifail analyze`
  inside a freshly cloned checkout compiles and runs that repository's regexes against your
  log — and nothing bounded how long one could take. A pattern with catastrophic backtracking,
  hostile or (far more likely) accidental, hung the CLI outright and pinned a request thread in
  `cifail serve`. A pattern that exceeds the budget is now skipped with a warning naming the
  rule, so a rule that has stopped working says so instead of disappearing. Ecosystem detection
  gained an overall time budget to go with its existing per-marker timeout, and the compiled-regex
  cache is keyed on the pattern rather than the rule id (two rules sharing an id used to make the
  second silently evaluate the first one's pattern). See SECURITY.md for the rule-pack trust model.

- **Eleven rules showed you their fix *template* instead of the fix.** Rules whose `match` is an
  alternation had `fix` text reading capture names that only one branch provides — so a log
  matching the other branch rendered `tsc rejected {file}:{line} — {code}: {message}` literally.
  It affected `typescript-compile-error` (on the `file.ts:12:5 - error TS2345` form modern `tsc`
  emits), three Elixir rules, three Scala rules, plus `helm-template-error`,
  `k8s-image-pull-failed`, `kotlin-jvm-target-mismatch` and `xctest-failed` — the last four on
  their *own* fixtures, i.e. every time they fired. Every fixture exercised only the first
  branch, and the breadth tests asserted the right rule won without ever checking that its fix
  rendered. Now: the tests assert no rendered fix contains a leftover placeholder, `rules
  validate` rejects a `fix` placeholder that any top-level alternation branch fails to capture
  (the obvious "does this group exist in the pattern" check passes on all eleven — the branch is
  the unit that has to satisfy the fix), and new fixtures cover the previously untested branches.

- **A passing test report produced no output at all.** `analyze --format junit --json` on a green
  report wrote nothing to stdout, so a downstream `jq` failed on empty input, and
  `--report sarif --report-out` never created the file — which breaks the
  `github/codeql-action/upload-sarif` chain the README documents, on exactly the runs where
  nothing is wrong. Both now emit an empty-but-valid document.

- `analyze --report-out out/report.sarif` no longer fails when `out/` does not exist; it creates
  the directory, as `gate --update` already did.

- `rules explain` reported a rule committed to a repository's `.cifail/rules/` as an
  `embedded default`. It checked `~/.cifail/rules` alone while loading from the full search
  path — so it was wrong for the newest tier and silent about it. It now names the directory the
  winning rule actually came from, and returns the documented exit code rather than a bare `0`.

- `analyze --server` silently ignored `--ai`, `--ai-provider`, `--ai-model` and `--top`, and
  skipped auto-resolution. They are now called out, as `--rules` already was.

- `cifail serve`: the sign-in route is rate-limited (it is necessarily public and compares a
  submitted string against the server token, so it was brute-forceable at network speed);
  `POST /analyze` caps the log body at 10 MB and answers `413`; `?limit=` is clamped on
  `/history` and available on `/repos/{repoId}/open`, which had no limit at all; the dashboard
  cookie expires after 12 hours and `POST /ui/logout` clears it. The notification dedupe map no
  longer grows without bound in a long-running server.

- **`github-actions-error` now recognizes the `::error::` form**, not just the `##[error]`
  one. They are two spellings of the same annotation — `::error::msg` is what a script
  *writes*, `##[error]msg` is how the runner *renders* it in the log you download from the
  Actions UI. A workflow that tees its own output, which is how this README and `action.yml`
  say to capture a log, keeps the `::` form, so cifail was missing the highest-signal line in
  exactly the logs it tells you to produce and falling through to the vague non-zero-exit rule.
  The annotation-properties spelling (`::error file=a.cs,line=12::msg`) matches too, without
  the properties leaking into the reported message.

- **A long absolute log path no longer vanishes from the report header.** Spectre word-wraps a
  rule title and keeps only the first line, so an absolute path — one long word — collapsed to
  `cifail ·…` and every report in a job that analyzed several logs looked identical. The label
  is now shortened from the left, keeping the file name (and, for an expanded test report, the
  test name), which is the part that says which log this is. CI passes absolute paths, so this
  was the common case rather than the rare one.

- **Ecosystem detection only knew each ecosystem's oldest tool.** A yarn or pnpm failure
  need never mention its own lockfile, a mypy or Ruff run has nothing but a `.py` suffix,
  and a SwiftPM build never says "xcodebuild" — so all of those scored below the threshold
  and fell back to `generic`. Tool invocations (`yarn install`, `npm run`, `npx`, `swift
  build`) and modern config files (`pyproject.toml`, `Package.swift`, `Podfile`) are now
  markers, alongside `mypy`/`ruff`/`poetry`/`flake8`/`pylint`.

- **Android and Scala builds now inherit the JVM rules**, because they are JVM builds. An
  Android job killed by the OOM killer used to get the vague `generic-oom` while
  `gradle-daemon-disappeared` sat unused in `java.yaml`. Inheritance is one-directional —
  a Maven build does not pick up Android's aapt2 rules.

- Gradle's `BUILD FAILED` was never a detection marker (only Maven's `BUILD FAILURE` was),
  so a Gradle-only log had nothing strong in it at all.

- `swift-compile-error` no longer restates every XCTest failure.

- **`cifail serve --help` crashed** (`Could not find color or style 'name'`, exit 70) in the
  Docker/full build. Spectre renders option descriptions as markup, and `--tokens-file`'s
  description contained a literal `[name]`. CI now builds the image and *runs* it, since a
  runtime base that cannot host the app still builds perfectly clean.

### Documentation

- The README now covers `cifail rules list|test|validate|explain` and `cifail suggest-rule`
  under [Teach it a new failure], along with `analyze --top`, `--no-git`, analyzing several
  logs in one run, and the `CIFAIL_AI_MAX_CALLS*` cost caps — all of which shipped
  undocumented.

- Added `SECURITY.md`, which states plainly that a stored analysis keeps a **log excerpt**,
  so your history database can contain whatever secrets were in the logs you analyzed.

- Added issue forms and a PR checklist. The rule-request form asks for a **redacted** log;
  the README previously asked for logs with no redaction guidance at all.

## [0.2.0] - 2026-08-06

The release that makes the advertised install actually work, and makes the CLI behave
under a pipe. No new commands beyond `cifail config` — this is the quality release.

### Breaking

- **`--type <unknown>` is now an error (exit 2)** instead of silently falling back to
  auto-detection. Most likely change to bite an existing CI job: a typo that used to be
  ignored now fails the step. The valid values are listed in `cifail analyze --help`.
- **Warnings and errors moved from stdout to stderr.** That was the bug —
  `cifail analyze --json | jq` could be corrupted by a single warning — but anyone
  grepping *stdout* for `warning:` needs to grep stderr now. `--annotations` moved too
  (the GitHub Actions runner scans both streams).
- **Some exit codes moved**, now that there is one taxonomy (see [Exit codes] in the
  README):
  - `history <id>` not found: `2` → `3`
  - `rules explain <id>` not found: `1` → `3`
  - `resolve` without a `--note`: `255` → `2`
  - `suggest-rule` with no AI model reachable: `1` → `6`
  - **`analyze` (0/1/2) and `rules validate` (0/1) are deliberately unchanged**, because
    the bundled GitHub Action and GitLab template branch on them.

### Added

- **`cifail config`** (alias **`cifail doctor`**) — answers "what is cifail actually
  doing?": version and build flavour (slim vs. full), every resolved path, which store
  and AI providers this binary actually has, and the effective database/AI/notification
  settings **each labelled with where it came from** (`config.yaml`, the env var by name,
  or `default`). Usually the answer to "I edited the config and nothing changed."
  It never prints a secret — only whether one is set. `--json`, `--strict`, `--path`.
- **Config linting.** A malformed `config.yaml` now reports the path and line/column
  instead of a raw YAML exception, and unknown keys are reported with a "did you mean"
  suggestion rather than being silently ignored. Surfaced by `cifail config`.
- **`.NET global tool`**: `dotnet tool install --global cifail`, published to
  [nuget.org](https://www.nuget.org/packages/cifail) via trusted publishing (OIDC — no
  long-lived API key exists for this repo).
- **Aggregate `SHA256SUMS`** on every release, alongside the existing per-artifact
  `.sha256` files.
- `cifail --version`, and an exit-code table in the README.

### Fixed

- **Every documented install path was broken.** The version was `0.1.0-alpha` while the
  tag was `v0.1.0`, so `publish.sh` built `cifail-0.1.0-alpha-<rid>.tar.gz` while
  `install.sh` asked for `cifail-0.1.0-<rid>.tar.gz` → 404. There is now exactly one
  version (`Directory.Build.props`), and CI fails the release if the tag disagrees.
- **`install.sh` was doubly broken on macOS**: it used GNU-only `grep -oP` to read the
  tag, and `sha256sum`, which macOS does not ship. Both are POSIX/BSD-safe now, checksum
  verification is mandatory (opt out with `CIFAIL_SKIP_CHECKSUM=1`), and a release is not
  published until the real script has installed the real assets on Linux **and** macOS.
- **Every Windows `.zip` shipped without a license** — the archive was created before
  `LICENSE`/`README.md` were copied in.
- **The Homebrew and Scoop manifests shipped literal `REPLACE_WITH_*_SHA256`
  placeholders.** They are now generated from the real checksums, and the generator fails
  if any placeholder survives.
- **The NuGet package was packed on every release and pushed nowhere** — the workflow
  read a secret that did not exist.
- **Three shipped rules could never have matched real tool output** (all three had passed
  review; only a realistic fixture caught them):
  - `generic-command-not-found` had the shell's word order reversed, and missed the
    `sh: 1: jq: not found` form that `sh -c` in a container actually prints.
  - `ruby-nomethod-error` used `.` to span a newline; the rule engine compiles with
    `Multiline`, not `Singleline`.
  - `composer-platform-requirement` had no room for composer's real wording,
    `requires PHP extension ext-intl`.
- **Ecosystem detection counted total matches, not distinct markers**, so thirty
  `[ERROR]` lines from a chatty logger outscored the markers that actually identified the
  log. Markers are now weighted and counted once each, with an explicit tie-break order.
- **`go-undefined` and `go-version-mismatch` had equal confidence**, so on a log
  containing both the winner was arbitrary.
- Unreachable `--server` hosts, expired tokens and unwritable report paths rendered a
  Spectre stack trace; they now produce one line on stderr and a meaningful exit code.
  `CIFAIL_DEBUG=1` opts back into the stack trace.
- A `401` from a `--server` now says to pass `--server-token`.
- The `/ui/*` routes returned `500` instead of `400` for a non-form POST.

### Changed

- **Every one of the 91 shipped rules now has a fixture proving it matches real tool
  output and *wins* the ranking** (62 had none), and a `docs:` link (53 had none). Both
  are enforced: the build fails for a rule with no fixture, and `rules validate` warns for
  a missing or non-http `docs:`.
- Rules that merely restated a more specific rule were narrowed (`go-build-failed`,
  `generic-docker-build`); rules that genuinely summarise were kept.
- Console glyphs (`✓ ✗ ⚠ • …`) degrade to ASCII when the console encoding cannot
  represent them, and output is UTF-8 without a BOM when redirected.
- `--db-provider`'s help text is honest about which providers this build actually has.

## [0.1.0] - 2026-07-08

First tagged release. **Superseded — do not install it**: its assets are named
`cifail-0.1.0-alpha-*` while the tag is `v0.1.0`, so nothing that follows the
documented install instructions can find them. Fixed in 0.2.0.

[Unreleased]: https://github.com/SebHenn/ci-failure-intelligence/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/SebHenn/ci-failure-intelligence/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/SebHenn/ci-failure-intelligence/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/SebHenn/ci-failure-intelligence/releases/tag/v0.1.0
[Exit codes]: https://github.com/SebHenn/ci-failure-intelligence#exit-codes
[Teach it a new failure]: https://github.com/SebHenn/ci-failure-intelligence#teach-it-a-new-failure
