#!/usr/bin/env bash
set -euo pipefail

# Redeploys the OTEL collector image on an existing VM.
#
# Use build/init/gcp-otel-collector-vm.sh for first-time infrastructure setup.
# This script intentionally leaves the VM, static IP, firewall rules, Caddy
# container, and Caddy /data certificate state alone.

PROJECT_ID="${GCP_PROJECT_ID:-zeeq-ai-prod}"
REGION="${GCP_REGION:-us-central1}"
ZONE="${GCP_ZONE:-us-central1-a}"
ARTIFACT_REPOSITORY="${GCP_ARTIFACT_REPOSITORY:-zeeq}"
INSTANCE_NAME="${ZEEQ_OTEL_COLLECTOR_VM_NAME:-zeeq-otel-collector}"
IMAGE="${ZEEQ_OTEL_COLLECTOR_IMAGE:-${REGION}-docker.pkg.dev/${PROJECT_ID}/${ARTIFACT_REPOSITORY}/zeeq-otel-collector:latest}"
ZEEQ_ISSUER_URL="${ZEEQ_OTEL_COLLECTOR_ISSUER_URL:-${ZEEQ_ISSUER_URL:-https://app.zeeq.ai/}}"
ZEEQ_OTLP_HTTP_ENDPOINT="${ZEEQ_OTEL_COLLECTOR_OTLP_HTTP_ENDPOINT:-${ZEEQ_OTLP_HTTP_ENDPOINT:-https://app.zeeq.ai}}"
ZEEQ_TELEMETRY_AUDIENCE="${ZEEQ_OTEL_COLLECTOR_TELEMETRY_AUDIENCE:-${ZEEQ_TELEMETRY_AUDIENCE:-https://app.zeeq.ai/mcp}}"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command gcloud

if ! gcloud compute instances describe "${INSTANCE_NAME}" \
  --project="${PROJECT_ID}" \
  --zone="${ZONE}" >/dev/null 2>&1; then
  echo "VM not found: ${INSTANCE_NAME} (${PROJECT_ID}/${ZONE})" >&2
  echo "Run build/init/gcp-otel-collector-vm.sh first." >&2
  exit 1
fi

echo "Redeploying Zeeq OTEL collector"
echo "Instance: ${INSTANCE_NAME}"
echo "Image: ${IMAGE}"

REMOTE_COMMAND="$(cat <<EOF
set -euo pipefail

# Root's home is read-only on Container-Optimized OS. Keep Docker auth config
# under /var/lib so Artifact Registry pulls work across boots and redeploys.
mkdir -p /var/lib/zeeq-docker-config

if command -v docker-credential-gcr >/dev/null 2>&1; then
  DOCKER_CONFIG=/var/lib/zeeq-docker-config docker-credential-gcr configure-docker --registries="${REGION}-docker.pkg.dev" >/dev/null
fi

DOCKER_CONFIG=/var/lib/zeeq-docker-config docker pull "${IMAGE}"
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker network create zeeq-otel >/dev/null 2>&1 || true

# Restart only the collector. Caddy owns DNS/certificate lifecycle and keeps its
# persistent /data state across collector deploys.
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker rm -f zeeq-otel-collector >/dev/null 2>&1 || true
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker run -d \\
  --name zeeq-otel-collector \\
  --restart unless-stopped \\
  --network zeeq-otel \\
  -e ZEEQ_ISSUER_URL="${ZEEQ_ISSUER_URL}" \\
  -e ZEEQ_TELEMETRY_AUDIENCE="${ZEEQ_TELEMETRY_AUDIENCE}" \\
  -e ZEEQ_OTLP_HTTP_ENDPOINT="${ZEEQ_OTLP_HTTP_ENDPOINT}" \\
  "${IMAGE}"

DOCKER_CONFIG=/var/lib/zeeq-docker-config docker ps --filter name=zeeq-otel-collector --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker logs --tail 80 zeeq-otel-collector
EOF
)"

gcloud compute ssh "${INSTANCE_NAME}" \
  --project="${PROJECT_ID}" \
  --zone="${ZONE}" \
  --command="bash -lc $(printf '%q' "${REMOTE_COMMAND}")"
