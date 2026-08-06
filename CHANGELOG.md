# Changelog

All notable changes to cifail are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and cifail
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is
below `1.0.0`, a **minor** bump may carry the breaking changes that a major bump would
after 1.0.

## [Unreleased]

### Added

- **`cifail gate`** — fail CI on a *new* failure while tolerating the known backlog, the way
  a linter baseline does. `cifail gate --update <logs>` accepts everything currently failing
  into a committed `.cifail/baseline.txt`; after that `cifail gate <logs>` exits 1 only for a
  fingerprint that isn't in it. The baseline is one fingerprint per line with the rule title
  as a comment, so it reads in review and deleting a line re-arms the gate. `gate` opens no
  store, reads no git, and makes no network call — its entire memory is that file, so it
  gives the same verdict on a laptop and in a scratch container. `--json`, `--format` and
  `--type` work as they do on `analyze`.

### Fixed

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

[Unreleased]: https://github.com/SebHenn/ci-failure-intelligence/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/SebHenn/ci-failure-intelligence/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/SebHenn/ci-failure-intelligence/releases/tag/v0.1.0
[Exit codes]: https://github.com/SebHenn/ci-failure-intelligence#exit-codes
[Teach it a new failure]: https://github.com/SebHenn/ci-failure-intelligence#teach-it-a-new-failure
