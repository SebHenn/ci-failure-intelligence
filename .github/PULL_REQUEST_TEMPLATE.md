<!--
Thanks for contributing! Delete whatever doesn't apply — this is a checklist, not a form.
-->

## What this changes

<!-- What broke or was missing, and what this does about it. Link the issue if there is one. -->

## Why this way

<!-- The reasoning that isn't obvious from the diff: what you ruled out, what the tradeoff is.
     This is the part that's worth writing; the diff already says what changed. -->

## How you verified it

<!-- The command you ran and what it printed. "Tests pass" is weaker than the failing case
     you reproduced first. -->

---

- [ ] `dotnet build` and `dotnet test` are green.
- [ ] `dotnet run --project src/CiFail.Cli -- rules validate src/CiFail.Core/rulepacks` exits 0.
- [ ] Output goes to the right stream — **stdout is the answer, stderr is everything about the
      run**. Commands use `CliConsole.Out` / `CliConsole.Err`, never `AnsiConsole.*` directly.
- [ ] Any new exit code comes from `Cli/ExitCodes.cs`; no bare ints.
- [ ] No secret can reach the console, a log, or a report.

### If you added or changed a rule

- [ ] The fixture is **real tool output**, copied verbatim — not written from the regex.
      (Three shipped rules once matched nothing real because their fixtures were written
      backwards from the pattern, and all three passed review.)
- [ ] There's a row in `RulePackBreadthTests` asserting your rule **wins** the ranking.
- [ ] The rule has a `docs:` link to the tool's own reference page.
- [ ] If it can fire on the same line as an existing rule, the more specific one has the
      higher `confidence` — equal confidence makes the winner arbitrary.

### If this is user-visible

- [ ] `README.md` covers it, and the wording stays plain ("What broke" / "How to fix it",
      confidence as high/medium/low).
- [ ] `CHANGELOG.md` has an entry under `## [Unreleased]`.
- [ ] `CLAUDE.md` reflects any new convention or gotcha a future contributor would trip on.
- [ ] Breaking change? Say so explicitly above — exit codes, flag behaviour and output
      streams are all things CI jobs depend on.
