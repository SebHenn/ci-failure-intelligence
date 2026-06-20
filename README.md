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

> Prefer Docker or a CI pipeline? See [Use cifail in CI](#use-cifail-in-ci) below.

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

### Handy options for `analyze`

- `--json` — print the result as JSON instead of a panel. Useful inside CI pipelines or
  other scripts.
- `--type dotnet|node|python|generic` — tell cifail what kind of log this is, if it
  guesses wrong.
- `--ai` — if a failure isn't recognized, also ask a local AI model for a suggestion.
  *(Optional and off by default — needs [Ollama](https://ollama.com) installed. Coming soon.)*
- `--no-history` — analyze without saving this run to history.

---

## Use cifail in CI

The quickest way is to pipe a failing step's output into cifail right in your pipeline.
For example, in GitHub Actions:

```yaml
- name: Build
  run: dotnet build 2>&1 | tee build.log

- name: Explain failures
  if: failure()
  run: cifail analyze build.log
```

A published **Docker image** and a ready-made **GitHub Action / GitLab template** are on
the roadmap below, so you won't even need to install the binary on the runner.

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
- **One-command install** as a native binary on any OS — no .NET needed. *(in progress)*
- **Pick your database** — keep the built-in SQLite, or point cifail at a shared
  PostgreSQL / MySQL / SQL Server / MongoDB. *(planned)*
- **Resolutions that record themselves** — cifail links a failure to the commit that
  fixed it, so you don't have to run `resolve` by hand. *(planned)*
- **Docker image, GitHub Action & GitLab template**, then a shared team service. *(planned)*
- **Optional local AI** suggestions via [Ollama](https://ollama.com) (`--ai`). *(planned)*

## License

[MIT](./LICENSE) — free to use, change, and share.
