#!/usr/bin/env bash
set -euo pipefail

PROJECT_ID="${GCP_PROJECT_ID:-zeeq-ai-prod}"
REGION="${GCP_REGION:-us-central1}"
REPOSITORY="${GCP_ARTIFACT_REPOSITORY:-zeeq}"
IMAGE_NAME="${ZEEQ_IMAGE_NAME:-zeeq-runtime}"
IMAGE_URI="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/${IMAGE_NAME}"
VERSION="${ZEEQ_VERSION:-${1:-}}"
VERSION_TAG="${ZEEQ_VERSION_TAG:-${2:-}}"

# Run from the root; local build-ship for now

if [[ -n "$VERSION" && -z "$VERSION_TAG" ]]; then
  VERSION_TAG="v${VERSION}"
fi

# NOTE: Release builds are produced by .github/workflows/release.yml and always
# pass SemVer metadata. This local helper keeps version args optional so local
# latest/dev image builds remain possible.

# Build the binaries as a check before Docker build
dotnet_build_args=(-p:GIT_SHA="$(git rev-parse HEAD)")
docker_build_args=()

if [[ -n "$VERSION" ]]; then
  dotnet_build_args+=("-p:ZEEQ_VERSION=${VERSION}")
  docker_build_args+=(--build-arg "ZEEQ_VERSION=${VERSION}")
fi

if [[ -n "$VERSION_TAG" ]]; then
  dotnet_build_args+=("-p:ZEEQ_VERSION_TAG=${VERSION_TAG}")
  docker_build_args+=(--build-arg "ZEEQ_VERSION_TAG=${VERSION_TAG}")
fi

dotnet build zeeq.slnx "${dotnet_build_args[@]}"

# Build the static assets as a check before Docker build
VITE_BASE=/web/ yarn --cwd ./src/web build

# Run the tests as a smoke check
dotnet test --solution zeeq.slnx --no-build --no-restore --disable-logo

GIT_SHA=$(git rev-parse HEAD)
SHORT_SHA="${GIT_SHA:0:8}"
tags=(-t "${IMAGE_URI}:sha-${SHORT_SHA}")

if [[ -n "$VERSION_TAG" ]]; then
  tags+=(-t "${IMAGE_URI}:${VERSION_TAG}")
else
  tags+=(-t "${IMAGE_URI}:latest")
fi

# Build and push the container
docker buildx build \
  --push \
  --platform linux/amd64 \
  --build-arg GIT_SHA="$GIT_SHA" \
  "${docker_build_args[@]}" \
  "${tags[@]}" \
  -f build/Dockerfile .
