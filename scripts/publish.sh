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
VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$PROJECT" | head -1)"
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

  # Package the single binary (plus license/readme) for distribution.
  base="cifail-$VERSION-$rid"
  if [[ "$rid" == win-* ]]; then
    ( cd "$out" && zip -q -j "../$base.zip" cifail.exe )
    cp LICENSE README.md "$out/" 2>/dev/null || true
    ( cd dist && sha256sum "$base.zip" > "$base.zip.sha256" )
  else
    cp LICENSE README.md "$out/" 2>/dev/null || true
    tar -C "$out" -czf "dist/$base.tar.gz" cifail LICENSE README.md
    ( cd dist && sha256sum "$base.tar.gz" > "$base.tar.gz.sha256" )
  fi
done

echo "==> done. artifacts in dist/"
ls -1 dist
