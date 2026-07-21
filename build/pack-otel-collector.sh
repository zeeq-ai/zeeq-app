#!/usr/bin/env bash
set -euo pipefail

# Builds and pushes the standalone Zeeq OTEL collector image.
#
# Unlike build/pack.sh for the application runtime, this script does not run
# .NET/Yarn builds or tests. The collector image packages a reviewed collector
# config on top of the upstream OpenTelemetry Collector contrib image.

PROJECT_ID="${GCP_PROJECT_ID:-zeeq-ai-prod}"
REGION="${GCP_REGION:-us-central1}"
REPOSITORY="${GCP_ARTIFACT_REPOSITORY:-zeeq}"
IMAGE_NAME="${ZEEQ_OTEL_COLLECTOR_IMAGE_NAME:-zeeq-otel-collector}"
IMAGE_URI="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/${IMAGE_NAME}"
COLLECTOR_BASE_IMAGE="${ZEEQ_OTEL_COLLECTOR_BASE_IMAGE:-otel/opentelemetry-collector-contrib:0.153.0}"
VERSION="${ZEEQ_VERSION:-${1:-}}"
VERSION_TAG="${ZEEQ_VERSION_TAG:-${2:-}}"

if [[ -n "$VERSION" && -z "$VERSION_TAG" ]]; then
  VERSION_TAG="v${VERSION}"
fi

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command docker
require_command git

if [[ ! -f build/otel-collector-config.gcp.yaml ]]; then
  echo "Missing collector config: build/otel-collector-config.gcp.yaml" >&2
  exit 1
fi

GIT_SHA="$(git rev-parse HEAD)"
SHORT_SHA="${GIT_SHA:0:8}"
tags=(-t "${IMAGE_URI}:sha-${SHORT_SHA}")

if [[ -n "$VERSION_TAG" ]]; then
  tags+=(-t "${IMAGE_URI}:${VERSION_TAG}")
else
  # latest is intentionally mutable for operator-driven VM redeploys. The
  # sha-* tag above remains the immutable rollback/audit reference.
  tags+=(-t "${IMAGE_URI}:latest")
fi

echo "Building Zeeq OTEL collector image"
echo "Image: ${IMAGE_URI}"
echo "Base: ${COLLECTOR_BASE_IMAGE}"
printf 'Tags:\n'
for ((i = 1; i < ${#tags[@]}; i += 2)); do
  printf '  %s\n' "${tags[$i]}"
done

docker buildx build \
  --push \
  --platform linux/amd64 \
  --build-arg "OTEL_COLLECTOR_BASE_IMAGE=${COLLECTOR_BASE_IMAGE}" \
  "${tags[@]}" \
  -f build/otel-collector/Dockerfile .
