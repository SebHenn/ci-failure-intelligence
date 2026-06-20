#!/usr/bin/env bash
#
# Install cifail. Detects your OS/CPU, downloads the matching release binary from
# GitHub, and drops it on your PATH. No .NET required.
#
#   curl -fsSL https://raw.githubusercontent.com/SebHenn/ci-failure-intelligence/main/scripts/install.sh | bash
#
# Env overrides:
#   CIFAIL_VERSION   release tag to install (default: latest)
#   CIFAIL_INSTALL_DIR  where to put the binary (default: ~/.local/bin)
#
set -euo pipefail

REPO="SebHenn/ci-failure-intelligence"
INSTALL_DIR="${CIFAIL_INSTALL_DIR:-$HOME/.local/bin}"

err() { echo "error: $*" >&2; exit 1; }

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
  version="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | grep -oP '(?<="tag_name": ")[^"]+')" \
    || err "could not determine the latest version. Set CIFAIL_VERSION explicitly."
fi
[ -n "$version" ] || err "no version found."

asset="cifail-${version#v}-${rid}.tar.gz"
url="https://github.com/$REPO/releases/download/$version/$asset"

echo "Installing cifail $version ($rid) -> $INSTALL_DIR"

# --- download + verify + install ---------------------------------------------
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$url" -o "$tmp/$asset" || err "download failed: $url"

if curl -fsSL "$url.sha256" -o "$tmp/$asset.sha256" 2>/dev/null; then
  ( cd "$tmp" && sha256sum -c "$asset.sha256" >/dev/null ) || err "checksum verification failed."
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
