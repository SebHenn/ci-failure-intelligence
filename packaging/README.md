# Packaging manifests

These are **templates**, not manifests. `scripts/render-packaging.sh` fills in the version and
the real SHA-256 checksums during a release, and the rendered `cifail.rb` / `cifail.json` are
attached to the GitHub Release as assets.

```bash
# what the release workflow runs (after scripts/publish.sh has produced dist/)
scripts/render-packaging.sh "$(scripts/version.sh)" dist packaging/rendered
```

Nothing here is hand-edited. Before 0.2.0 both manifests were maintained by hand and shipped
with literal `REPLACE_WITH_*_SHA256` placeholders, so `scoop install` and `brew install` could
never have succeeded. The renderer now **fails the release** if any placeholder token survives.

## Where each one is consumed

- **Scoop** — the README points at
  `https://github.com/SebHenn/ci-failure-intelligence/releases/latest/download/cifail.json`,
  so the manifest a user installs always matches the release it came from. Nothing needs to be
  committed back to `main`.
- **Homebrew** — `brew install SebHenn/tap/cifail` reads a *separate* repository,
  `SebHenn/homebrew-tap`. The rendered `cifail.rb` is attached to the release; publishing it to
  the tap is a manual step (or a future secret-gated workflow step) — this repo cannot update
  another repo without a token.

## Testing a render without cutting a release

`tests/fixtures/dist-fake/` holds checksum files for a fake `9.9.9` build, so CI can exercise
templating offline:

```bash
scripts/render-packaging.sh 9.9.9 tests/fixtures/dist-fake /tmp/out
```
