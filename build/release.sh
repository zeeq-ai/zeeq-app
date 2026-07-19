#!/usr/bin/env bash
set -euo pipefail

channel="${1:-}"
version_override="${2:-}"

if [[ "$channel" != "stable" && "$channel" != "rc" ]]; then
  echo "Usage: ./build/release.sh stable|rc [version_override]" >&2
  exit 1
fi

args=(release.yml --ref main -f "channel=${channel}")

if [[ -n "$version_override" ]]; then
  args+=(-f "version_override=${version_override}")
fi

gh workflow run "${args[@]}"
gh run list --workflow release.yml --limit 5
