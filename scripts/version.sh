#!/usr/bin/env bash
#
# Print cifail's version — the single source of truth, from Directory.Build.props.
#
# Everything that needs a version calls this: publish.sh (artifact names), check-versions.sh
# (drift guard), release.yml. Nothing else may parse the version out of a file.
#
set -euo pipefail

cd "$(dirname "$0")/.."

# Ask MSBuild for the evaluated property rather than pattern-matching XML. This stays correct
# if the version ever moves behind a condition or an import, and it is exactly the value that
# `dotnet publish`/`dotnet pack` will stamp into the binary.
version=""
msbuild_ok=0
if msbuild_out="$(dotnet msbuild src/CiFail.Cli/CiFail.Cli.csproj -getProperty:Version -nologo 2>&1)"; then
  msbuild_ok=1
  version="$(printf '%s' "$msbuild_out" | tr -d '[:space:]')"
fi

# Fallback for SDKs older than 8.0.200, which have no -getProperty. Deliberately POSIX sed
# (not `grep -oP`, which is GNU-only and would break on macOS) against the one file that is
# guaranteed to hold exactly one <Version>.
#
# Warn loudly when falling back: a *broken* props file also lands here, and a silent fallback
# would happily print the right number while every real build is failing.
if [ -z "$version" ]; then
  echo "warning: could not evaluate the version via MSBuild; falling back to reading Directory.Build.props." >&2
  if [ "$msbuild_ok" -eq 0 ]; then
    printf '%s\n' "$msbuild_out" | head -3 >&2
  fi
  version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
fi

if [ -z "$version" ]; then
  echo "error: could not determine the version from Directory.Build.props" >&2
  exit 1
fi

printf '%s\n' "$version"
