#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build/gcp-runtime-shared.sh
source "${SCRIPT_DIR}/gcp-runtime-shared.sh"

# Plain environment variables passed to the worker pool, as KEY=VALUE.
# AppSettings__Ingest__ContentRootPath points LocalTempWorkspaceProvider at the
# ephemeral disk mount below instead of the OS temp directory.
env_vars=(
  "${ZEEQ_RUNTIME_COMMON_ENV_VARS[@]}"
  "ZEEQ_RUN_MODE=worker"
  "ZEEQ_MESSAGING_ROLE=producer-consumer"
  "AppSettings__Ingest__ContentRootPath=${ZEEQ_INGEST_MOUNT_PATH}"
)

# Deploy the background worker runtime. Worker pools are non-HTTP Cloud Run
# runtimes, which matches Zeeq's generic-host worker process.
#
# NOTE: ingest workspaces are deleted on dispose regardless of storage medium,
# so repeat syncs re-clone. The ephemeral disk avoids GCS FUSE API throttling
# during git's filesystem-heavy clone/checkout path.
gcloud beta run worker-pools deploy zeeq-worker \
  --image="${ZEEQ_RUNTIME_IMAGE}" \
  --add-cloudsql-instances="${ZEEQ_CLOUDSQL_INSTANCE}" \
  --add-volume="name=ingest,type=ephemeral-disk,size=${ZEEQ_INGEST_EPHEMERAL_DISK_SIZE}" \
  --add-volume-mount="volume=ingest,mount-path=${ZEEQ_INGEST_MOUNT_PATH}" \
  --instances=1 \
  --region="${REGION}" \
  --cpu=2 \
  --memory=4Gi \
  --project="${PROJECT_ID}" \
  --set-secrets="$(join_by_comma "${ZEEQ_RUNTIME_WORKER_SECRETS[@]}")" \
  --set-env-vars="$(join_by_comma "${env_vars[@]}")"
