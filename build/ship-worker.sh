#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build/gcp-runtime-shared.sh
source "${SCRIPT_DIR}/gcp-runtime-shared.sh"

# Plain environment variables passed to the worker pool, as KEY=VALUE.
# AppSettings__Ingest__ContentRootPath points LocalTempWorkspaceProvider at the
# GCS FUSE mount below instead of the OS temp directory — no dispatcher code
# change needed; it already reads its workspace root from this setting.
env_vars=(
  "${ZEEQ_RUNTIME_COMMON_ENV_VARS[@]}"
  "ZEEQ_RUN_MODE=worker"
  "ZEEQ_MESSAGING_ROLE=producer-consumer"
  "AppSettings__Ingest__ContentRootPath=${ZEEQ_INGEST_MOUNT_PATH}"
)

# Deploy the background worker runtime. Worker pools are non-HTTP Cloud Run
# runtimes, which matches Zeeq's generic-host worker process.
#
# NOTE: the mounted bucket is provisioned separately by build/init/gcp-storage.sh
# (one-time setup, not part of this deploy). Ingest workspaces are deleted on
# dispose regardless of storage medium (LocalIngestWorkspace's local-temp
# semantics, unchanged here) — a run re-clones rather than reuses a prior pull.
# Reusing clones across runs on the mount is deferred to a dedicated
# MountedVolumeWorkspaceProvider (plan Phase 3.2), not needed to unblock
# testing on GCP.
# NOTE: --instances=1 below is a current isolation assumption, not just a
# throughput choice. All replicas would share this same mounted bucket
# namespace (unlike local temp disk, where each replica had its own isolated
# /tmp) — the existing "already in flight" check on a library's sync status
# guards concurrent syncs of the *same* library, but scaling this beyond 1
# instance should revisit shared-mount concurrency for the ingest workspace
# path scheme first.
gcloud beta run worker-pools deploy zeeq-worker \
  --image="${ZEEQ_RUNTIME_IMAGE}" \
  --add-cloudsql-instances="${ZEEQ_CLOUDSQL_INSTANCE}" \
  --add-volume="name=ingest,type=cloud-storage,bucket=${ZEEQ_INGEST_BUCKET}" \
  --add-volume-mount="volume=ingest,mount-path=${ZEEQ_INGEST_MOUNT_PATH}" \
  --instances=1 \
  --region="${REGION}" \
  --cpu=2 \
  --memory=4Gi \
  --project="${PROJECT_ID}" \
  --set-secrets="$(join_by_comma "${ZEEQ_RUNTIME_WORKER_SECRETS[@]}")" \
  --set-env-vars="$(join_by_comma "${env_vars[@]}")"
