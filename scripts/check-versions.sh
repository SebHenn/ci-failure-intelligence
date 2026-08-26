#!/usr/bin/env bash
#
# Guard against version drift. This exists because cifail shipped a release where
# Directory.Build.props said one thing and the git tag said another, which made every
# documented install path 404: publish.sh named artifacts after the project version while
# install.sh asked for the tag. They must agree by construction, so CI checks it.
#
# Usage:
#   scripts/check-versions.sh            # local/PR check
#   scripts/check-versions.sh v0.2.0     # release check: the tag must match too
#
set -euo pipefail

cd "$(dirname "$0")/.."

# Invoked through `bash` rather than directly: this repo is developed on Windows, where git
# does not track the executable bit, so a fresh clone can easily have mode 644 here.
version="$(bash scripts/version.sh)"
fail=0

note() { echo "error: $*" >&2; fail=1; }

# 1. Exactly one place may declare a version.
strays="$(grep -rl '<Version>' src tests --include='*.csproj' 2>/dev/null || true)"
if [ -n "$strays" ]; then
  note "these projects declare their own <Version>; the only source of truth is Directory.Build.props:"
  printf '%s\n' "$strays" | while IFS= read -r stray; do
    echo "  $stray" >&2
  done
fi

# 2. The Helm chart's appVersion tracks the CLI version (the chart's own `version:` is
#    independent and hand-managed).
chart_file="deploy/helm/cifail/Chart.yaml"
chart_app="$(sed -n 's/^appVersion:[[:space:]]*"\{0,1\}\([^"]*\)"\{0,1\}[[:space:]]*$/\1/p' "$chart_file" | head -1)"
if [ "$chart_app" != "$version" ]; then
  note "$chart_file appVersion is '$chart_app' but the version is '$version'"
fi

# 3. The composite action stamps its own version so its output can name the build that ran
#    (issue #21 — `@v1` and `:latest` are both moving references, so neither identifies one).
#    A stamp nobody checks is a stamp that lies, and it would lie in exactly the situation it
#    exists for: someone trying to work out which build they are on.
action_file="action.yml"
action_stamp="$(sed -n "s/^[[:space:]]*CIFAIL_ACTION_VERSION:[[:space:]]*['\"]\{0,1\}\([^'\"]*\)['\"]\{0,1\}[[:space:]]*$/\1/p" "$action_file" | head -1)"
if [ -z "$action_stamp" ]; then
  note "$action_file has no CIFAIL_ACTION_VERSION stamp; the action could not report its version"
elif [ "$action_stamp" != "$version" ]; then
  note "$action_file CIFAIL_ACTION_VERSION is '$action_stamp' but the version is '$version'"
fi

# 4. On a release, the tag must match the version. Accepts v-prefixed and bare tags.
#    An empty argument means "not a tag build" (the release workflow passes "" on a manual
#    dispatch), so it must be treated as no-tag rather than as a tag that matches nothing.
if [ "${1:-}" != "" ]; then
  tag="$1"
  if [ "${tag#v}" != "$version" ]; then
    note "tag '$tag' does not match version '$version' — artifact names would not resolve"
  fi
fi

if [ "$fail" -eq 0 ]; then
  echo "versions agree: $version"
fi
exit "$fail"
