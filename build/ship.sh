#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build/gcp-runtime-shared.sh
source "${SCRIPT_DIR}/gcp-runtime-shared.sh"

# Generate and upload certs with:
#   ./build/certs/gen-openiddict-certs.sh && ./build/certs/upload-openiddict-secrets.sh

# Plain environment variables passed to the web service container, as KEY=VALUE.
env_vars=(
  "${ZEEQ_RUNTIME_COMMON_ENV_VARS[@]}"
  "${ZEEQ_RUNTIME_OPENIDDICT_CERTIFICATE_ENV_VARS[@]}"
  "ZEEQ_RUN_MODE=web"
  "ZEEQ_MESSAGING_ROLE=producer"
)

# Deploy the HTTP web runtime. The web service only publishes messages; the
# worker pool owns consumption and any follow-up publishing from handlers.
gcloud run deploy zeeq-runtime \
  --image="${ZEEQ_RUNTIME_IMAGE}" \
  --allow-unauthenticated \
  --add-cloudsql-instances="${ZEEQ_CLOUDSQL_INSTANCE}" \
  --min-instances=0 \
  --max-instances=4 \
  --timeout=15m \
  --region="${REGION}" \
  --cpu-boost \
  --cpu=2 \
  --memory=2Gi \
  --concurrency=250 \
  --use-http2 \
  --project="${PROJECT_ID}" \
  --set-secrets="$(join_by_comma "${ZEEQ_RUNTIME_WEB_SECRETS[@]}")" \
  --set-env-vars="$(join_by_comma "${env_vars[@]}")"
