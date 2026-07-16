#!/usr/bin/env bash
set -euo pipefail

IMAGE="${ZEEQ_POSTGRES_IMAGE:-ghcr.io/zeeq-ai/zeeq-postgres:pg18}"
PLATFORMS="${ZEEQ_POSTGRES_PLATFORMS:-linux/amd64,linux/arm64}"

docker buildx build \
  --platform "${PLATFORMS}" \
  --tag "${IMAGE}" \
  --push \
  build/postgres
