#!/usr/bin/env bash
#
# Install cifail. Detects your OS/CPU, downloads the matching release binary from
# GitHub, and drops it on your PATH. No .NET required.
#
#   curl -fsSL https://raw.githubusercontent.com/SebHenn/ci-failure-intelligence/main/scripts/install.sh | bash
#
# Env overrides:
#   CIFAIL_VERSION        release tag to install (default: latest)
#   CIFAIL_INSTALL_DIR    where to put the binary (default: ~/.local/bin)
#   CIFAIL_SKIP_CHECKSUM  set to 1 to install without verifying (not recommended)
#   CIFAIL_BASE_URL       where to fetch artifacts from (default: the GitHub release).
#                         The release workflow points this at a local directory (file://)
#                         so it can smoke-test THIS script against a draft release, rather
#                         than reimplementing its logic and testing nothing.
#
set -euo pipefail

REPO="SebHenn/ci-failure-intelligence"
INSTALL_DIR="${CIFAIL_INSTALL_DIR:-$HOME/.local/bin}"

err() { echo "error: $*" >&2; exit 1; }

# macOS ships BSD tools: no `sha256sum` (it's `shasum -a 256`) and no `grep -P`. Everything
# below sticks to what both GNU and BSD userlands provide.
sha256_verify() {  # $1 = checksum file, run from its directory
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c "$1" >/dev/null
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 -c "$1" >/dev/null
  else
    return 2  # no tool available
  fi
}

# --- detect platform -> RID --------------------------------------------------
os="$(uname -s)"
arch="$(uname -m)"
case "$os" in
  Linux)  rid_os="linux" ;;
  Darwin) rid_os="osx" ;;
  *)      err "unsupported OS '$os'. On Windows, use the Scoop manifest or download the .zip from Releases." ;;
esac
case "$arch" in
  x86_64|amd64)  rid_arch="x64" ;;
  arm64|aarch64) rid_arch="arm64" ;;
  *)             err "unsupported CPU architecture '$arch'." ;;
esac
rid="${rid_os}-${rid_arch}"

# --- resolve version ---------------------------------------------------------
version="${CIFAIL_VERSION:-}"
if [ -z "$version" ]; then
  # POSIX sed, not `grep -oP` — the latter is GNU-only and silently breaks this whole
  # script on macOS, where the README tells people to run exactly this command.
  version="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | sed -n 's/.*"tag_name":[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)" \
    || err "could not determine the latest version. Set CIFAIL_VERSION explicitly."
fi
[ -n "$version" ] || err "no version found."

asset="cifail-${version#v}-${rid}.tar.gz"
base_url="${CIFAIL_BASE_URL:-https://github.com/$REPO/releases/download/$version}"
url="$base_url/$asset"

echo "Installing cifail $version ($rid) -> $INSTALL_DIR"

# --- download + verify + install ---------------------------------------------
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$url" -o "$tmp/$asset" || err "download failed: $url"

# Verification is mandatory. It used to be skipped silently whenever the .sha256 fetch
# failed, which meant a network hiccup downgraded you to an unverified binary without
# saying so. Opt out explicitly with CIFAIL_SKIP_CHECKSUM=1 if you must.
if [ "${CIFAIL_SKIP_CHECKSUM:-}" = "1" ]; then
  echo "WARNING: skipping checksum verification (CIFAIL_SKIP_CHECKSUM=1)." >&2
else
  curl -fsSL "$url.sha256" -o "$tmp/$asset.sha256" \
    || err "could not download the checksum ($url.sha256). Re-run, or set CIFAIL_SKIP_CHECKSUM=1 to bypass."
  rc=0
  ( cd "$tmp" && sha256_verify "$asset.sha256" ) || rc=$?
  case "$rc" in
    0) ;;
    2) err "no sha256sum/shasum available to verify the download. Install one, or set CIFAIL_SKIP_CHECKSUM=1." ;;
    *) err "checksum verification FAILED for $asset — do not use this download." ;;
  esac
fi

tar -C "$tmp" -xzf "$tmp/$asset"
mkdir -p "$INSTALL_DIR"
install -m 0755 "$tmp/cifail" "$INSTALL_DIR/cifail"

echo "Installed: $INSTALL_DIR/cifail"
case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) echo "NOTE: $INSTALL_DIR is not on your PATH. Add it, e.g.:"
     echo "  echo 'export PATH=\"$INSTALL_DIR:\$PATH\"' >> ~/.bashrc && source ~/.bashrc" ;;
esac
echo "Try it:  cifail --help"
