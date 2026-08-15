# Security policy

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub:
[**Report a vulnerability**](https://github.com/SebHenn/ci-failure-intelligence/security/advisories/new).
That opens a draft advisory only you and the maintainers can see.

Include what you'd want if you were fixing it: the version (`cifail --version`), the
platform, what an attacker can do, and the smallest reproduction you have. If a log is
part of the reproduction, **redact it first** — see [Logs are untrusted input](#logs-are-untrusted-input).

Expect an acknowledgement within a week. Once a fix is ready it ships in a patch release
and the advisory is published with credit, unless you'd rather not be named.

## Supported versions

cifail is pre-1.0 and there is no long-term support branch: **fixes go into the next
release from `main`**. Only the latest release is supported.

| Version | Supported |
| --- | --- |
| 0.3.x   | ✅ |
| 0.2.x   | ❌ — superseded. Note 0.2.x runs rule-pack regexes with **no timeout**; see [Rule packs execute nothing — but their regexes do run](#rule-packs-execute-nothing--but-their-regexes-do-run). |
| 0.1.0   | ❌ — superseded, and its release assets never resolved (see [CHANGELOG.md](CHANGELOG.md)) |

## What cifail does with your data

cifail is **local-first and offline by default**. It makes no network calls of its own
unless you turn one of these on:

| Feature | Network | Default |
| --- | --- | --- |
| `analyze`, `history`, `stats`, `clusters`, `rules`, `gate` | none | — |
| `--server` / `serve` | your own cifail server | off |
| AI suggestions, embeddings, `suggest-rule` | your Ollama host (localhost by default) | off |
| Notifications (Slack, webhook, Discord, Teams, SMTP, GitHub issues) | the endpoint you configure, **server-side only** | off |

There is **no telemetry, no crash reporting, and no usage reporting** — not opt-out, none
at all. Nothing is sent anywhere you did not configure.

### Logs are untrusted input

The one thing worth understanding: **CI logs frequently contain secrets** — tokens echoed
by a misbehaving step, connection strings in a stack trace, signed URLs in a curl trace.

When cifail records a failure it persists an **excerpt of the matched log region** and a
bag of terms from it. So:

- `~/.cifail/history.db` (or your configured database) **can contain whatever secrets
  were in the logs you analyzed.** Treat it with the same care as the logs themselves.
  `cifail analyze --no-history` still compares against history but does not write the
  current run to it.
- Fingerprints and similarity run over a *scrubbed* form of the log (paths, numbers and
  GUIDs collapsed). **Scrubbing is for matching, not for redaction** — do not rely on it
  to remove secrets.
- Anything you attach to a **public** issue, including a log for a rule request, is
  public forever. Redact before you paste.

`cifail config` deliberately **never prints a secret** — it reports only whether one is
set, and which env var it came from. A test plants a password and a webhook URL and
asserts neither reaches stdout or stderr.

### Running `cifail serve`

`serve` is the only component that listens on a network. It is meant for a trusted
network or behind an authenticating proxy, and:

- **Started without a token it runs open** — every route except the probes and the sign-in
  flow is unauthenticated — and logs a loud warning. Always set `CIFAIL_SERVER_TOKEN`
  (or `--token`) in anything that isn't a laptop.
- Prefer **per-client tokens** (`CIFAIL_SERVER_TOKENS`, `--tokens-file`) so one client can
  be revoked without rotating everyone's. All comparisons are constant-time.
- **Mutual TLS** is available (`--client-ca <pem> --tls-cert <pfx>`): clients whose
  certificate does not chain to your CA are rejected at the handshake.
- The **sign-in route is rate-limited** (10 attempts per minute per client address). It has
  to be public — a browser signing in has no cookie yet — so without a limiter the server
  token could be guessed at network speed.
- The dashboard's session cookie is HttpOnly and `SameSite=Strict`, `Secure` over HTTPS,
  and **expires after 12 hours**; `POST /ui/logout` clears it. Note the cookie value *is*
  the bearer token, so treat it as one.
- `POST /analyze` **caps the log it will accept at 10 MB** and answers `413` beyond that;
  the list endpoints clamp `?limit=`. Unbounded, either was a memory-exhaustion lever for
  anyone who could reach the port.
- `/healthz` (liveness) never touches the store; `/readyz` (readiness) does. Both are
  public, because the kubelet has no token.
- The API serves stored **log excerpts**, so anyone who can read the API can read those
  excerpts. See above.

Not yet addressed: the API is not rate-limited beyond the sign-in route, and notifications
are dispatched on the request thread, so a slow webhook slows the `/analyze` that triggered
it.

See [deploy/README.md](deploy/README.md) for the deployment wiring.

### Supply chain

- Releases are built by [`.github/workflows/release.yml`](.github/workflows/release.yml)
  on GitHub-hosted runners from a tagged commit. Nothing is built or signed locally.
- Every binary archive ships a `.sha256`, and each release an aggregate `SHA256SUMS`.
  `scripts/install.sh` **verifies the checksum and refuses to install without it**
  (override only with `CIFAIL_SKIP_CHECKSUM=1`).
- The NuGet package is published with **trusted publishing (OIDC)**: no long-lived NuGet
  API key exists for this repository, so there is none to leak.
- Binaries are **not** code-signed or notarized yet. macOS Gatekeeper and Windows
  SmartScreen will warn on first run.

### Rule packs execute nothing — but their regexes do run

Rules are data — YAML with a regex, a title and some prose. cifail never executes anything
from a rule pack, and never runs a command from a log. A malformed regex is skipped rather
than being fatal, and `cifail rules validate` lints a pack before you trust it.

**cifail loads rule packs from the repository you are working in.** Alongside
`~/.cifail/rules/`, it walks up from the working directory looking for `.cifail/rules/`,
and also honours `rules.paths` in `config.yaml`, the `CIFAIL_RULES` environment variable
and `--rules`. So running `cifail analyze` inside a checkout you just cloned will compile
and run that repository's regexes against your log. That is the point of the feature — a
rule like "the determinism contract is broken" belongs in the repo it describes — but it
means **a rule pack is code you obtained from someone else, in the sense that matters for
denial of service**.

The guard is a time limit: every rule pattern runs under a **2-second match timeout**
(`RuleEngine.MatchTimeout`), and a pattern that exceeds it is skipped with a warning naming
the rule rather than hanging the process. Ecosystem detection has both a per-marker timeout
and an overall budget. Drafts from `suggest-rule` are additionally compiled under a
1-second timeout before they can be written.

Two things this does **not** protect against, so read an unfamiliar pack first:

- A rule's `fix` text is advice a human may paste into a terminal.
- A rule can be written to match your log and claim whatever it likes about it.

`cifail gate` deliberately opens no store and makes no network call, so a repo-local pack
influencing a gate verdict cannot also exfiltrate anything.
