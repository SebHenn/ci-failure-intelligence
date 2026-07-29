#!/usr/bin/env bash
#
# Build self-contained, single-file cifail binaries for every supported platform.
# Output: dist/<rid>/cifail[.exe]  and  dist/cifail-<version>-<rid>.{tar.gz,zip} + .sha256
#
# Usage:
#   scripts/publish.sh                # all RIDs
#   scripts/publish.sh linux-x64      # one RID
#
set -euo pipefail

cd "$(dirname "$0")/.."

PROJECT="src/CiFail.Cli/CiFail.Cli.csproj"
# The single source of truth (Directory.Build.props, via MSBuild). Overridable for dry runs.
# These artifact names must match what scripts/install.sh asks for; check-versions.sh keeps
# the version and the release tag in agreement so they line up by construction.
VERSION="${CIFAIL_VERSION:-$(bash scripts/version.sh)}"
VERSION="${VERSION#v}"
ALL_RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
RIDS=("${@:-${ALL_RIDS[@]}}")

rm -rf dist
mkdir -p dist

for rid in "${RIDS[@]}"; do
  echo "==> publishing $rid"
  out="dist/$rid"
  dotnet publish "$PROJECT" \
    --configuration Release \
    --runtime "$rid" \
    --output "$out" \
    -p:Version="$VERSION"

  # Package the single binary plus license/readme. Copy the legal files FIRST — the win-*
  # branch used to zip before copying, so every .zip shipped without a LICENSE. No
  # `|| true` here either: a missing LICENSE must fail the build, not vanish.
  base="cifail-$VERSION-$rid"
  cp LICENSE README.md "$out/"
  if [[ "$rid" == win-* ]]; then
    ( cd "$out" && zip -q -j "../$base.zip" cifail.exe LICENSE README.md )
    ( cd dist && sha256sum "$base.zip" > "$base.zip.sha256" )
  else
    tar -C "$out" -czf "dist/$base.tar.gz" cifail LICENSE README.md
    ( cd dist && sha256sum "$base.tar.gz" > "$base.tar.gz.sha256" )
  fi
done

# Aggregate manifest, in addition to (never instead of) the per-artifact .sha256 files that
# install.sh and any existing user scripts already consume. nullglob keeps a single-RID run
# working (no .zip exists when only a linux RID was built) without masking real errors.
(
  cd dist
  shopt -s nullglob
  artifacts=(cifail-*.tar.gz cifail-*.zip)
  if [ ${#artifacts[@]} -gt 0 ]; then
    sha256sum "${artifacts[@]}" > SHA256SUMS
  fi
)

echo "==> done. artifacts in dist/"
ls -1 dist
