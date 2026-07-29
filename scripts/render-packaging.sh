#!/usr/bin/env bash
#
# Render the Homebrew formula and Scoop manifest from their templates, filling in the real
# version and the real SHA-256 checksums produced by scripts/publish.sh.
#
# This exists because both manifests were hand-maintained and shipped for months with literal
# REPLACE_WITH_*_SHA256 placeholders, which meant `scoop install` and `brew install` could
# never have worked. The guard at the bottom makes that failure mode impossible.
#
# Usage:
#   scripts/render-packaging.sh <version> <dist-dir> <out-dir>
#   scripts/render-packaging.sh 0.2.0 dist packaging/rendered
#
set -euo pipefail

if [ $# -ne 3 ]; then
  echo "usage: $0 <version> <dist-dir> <out-dir>" >&2
  exit 2
fi

version="${1#v}"
dist="$2"
out="$3"

cd "$(dirname "$0")/.."
mkdir -p "$out"

# Read the checksum for one artifact. The .sha256 files hold "<hash> *<filename>"; we want
# field 1. Missing files are fatal — a manifest with a blank hash is worse than no manifest.
sha_for() {  # $1 = artifact filename
  local f="$dist/$1.sha256"
  [ -f "$f" ] || { echo "error: missing checksum file: $f" >&2; exit 1; }
  local hash
  hash="$(awk '{print $1}' "$f" | head -1)"
  [ -n "$hash" ] || { echo "error: empty checksum in $f" >&2; exit 1; }
  printf '%s' "$hash"
}

osx_arm64="$(sha_for "cifail-$version-osx-arm64.tar.gz")"
osx_x64="$(sha_for "cifail-$version-osx-x64.tar.gz")"
linux_arm64="$(sha_for "cifail-$version-linux-arm64.tar.gz")"
linux_x64="$(sha_for "cifail-$version-linux-x64.tar.gz")"
win_x64="$(sha_for "cifail-$version-win-x64.zip")"
win_arm64="$(sha_for "cifail-$version-win-arm64.zip")"

render() {  # $1 = template, $2 = destination
  sed \
    -e "s|@@VERSION@@|$version|g" \
    -e "s|@@SHA256_OSX_ARM64@@|$osx_arm64|g" \
    -e "s|@@SHA256_OSX_X64@@|$osx_x64|g" \
    -e "s|@@SHA256_LINUX_ARM64@@|$linux_arm64|g" \
    -e "s|@@SHA256_LINUX_X64@@|$linux_x64|g" \
    -e "s|@@SHA256_WIN_X64@@|$win_x64|g" \
    -e "s|@@SHA256_WIN_ARM64@@|$win_arm64|g" \
    "$1" > "$2"
}

render packaging/homebrew/cifail.rb.template "$out/cifail.rb"
render packaging/scoop/cifail.json.template  "$out/cifail.json"

# The whole point: never ship an unsubstituted placeholder.
if grep -l '@@' "$out/cifail.rb" "$out/cifail.json" 2>/dev/null | grep -q .; then
  echo "error: unsubstituted @@TOKEN@@ left in the rendered manifests:" >&2
  grep -n '@@' "$out/cifail.rb" "$out/cifail.json" >&2 || true
  exit 1
fi

# The Scoop manifest must still be valid JSON after templating.
if command -v jq >/dev/null 2>&1; then
  jq empty "$out/cifail.json" || { echo "error: rendered Scoop manifest is not valid JSON" >&2; exit 1; }
fi

echo "rendered $out/cifail.rb and $out/cifail.json for $version"
