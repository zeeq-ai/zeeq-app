#!/usr/bin/env bash
set -euo pipefail

# Dispatch the production release workflow from a local shell.
#
# Use this when an operator wants to ship the current `main` branch without
# opening the GitHub Actions UI. The workflow still runs in GitHub Actions and
# performs the normal release plan, validation, image publish, Cloud Run deploy,
# and GitHub Release publishing steps.
#
# The first argument chooses the release channel:
#   stable - publish a stable SemVer tag, for example v1.0.0
#   rc     - publish the next release-candidate tag, for example v1.0.0-rc.1
#
# The optional second argument overrides the calculated SemVer version. Provide
# the version without a leading "v"; the workflow will add the tag prefix.
#
# Examples:
#   ./build/release.sh rc
#   ./build/release.sh rc 1.0.0-rc.3
#   ./build/release.sh stable
#   ./build/release.sh stable 1.0.0
#
# Prerequisites:
#   - GitHub CLI (`gh`) is installed and authenticated for zeeq-ai/zeeq-app.
#   - The production GitHub environment variables for GCP Workload Identity are
#     configured, because the workflow performs the actual cloud deployment.
channel="${1:-}"
version_override="${2:-}"

# Keep the local entrypoint deliberately small: it validates only the public
# script contract, then lets .github/workflows/release.yml perform the release
# calculation and deployment checks in the environment where the release runs.
if [[ "$channel" != "stable" && "$channel" != "rc" ]]; then
  echo "Usage: ./build/release.sh stable|rc [version_override]" >&2
  exit 1
fi

args=(release.yml --ref main -f "channel=${channel}")

# version_override is intentionally optional. When omitted, the workflow uses
# paulhatch/semantic-version to calculate the next base version from commits
# since the previous v* tag, and for rc releases it then selects the next rc.N.
if [[ -n "$version_override" ]]; then
  args+=(-f "version_override=${version_override}")
fi

# Queue the workflow and show the most recent release runs so the operator can
# open the new run or confirm that GitHub accepted the dispatch request.
gh workflow run "${args[@]}"
gh run list --workflow release.yml --limit 5
