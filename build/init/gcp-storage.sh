set -e

# Provisions the GCS bucket used by Cloud Storage FUSE runtime mounts.
# Git clone ingest no longer uses this bucket; build/ship-worker.sh mounts a
# Cloud Run ephemeral disk at AppSettings__Ingest__ContentRootPath instead.

PROJECT=$GCP_PROJECT_ID # Set in .config/mise.toml
RUNTIME_SA=$GCP_RUNTIME_SA # Set via env var
REGION=us-central1
BUCKET="${PROJECT}-ingest"

gcloud services enable storage.googleapis.com \
  --project="$PROJECT"

gcloud storage buckets create "gs://${BUCKET}" \
  --project="$PROJECT" \
  --location="$REGION" \
  --uniform-bucket-level-access

# Keep a short lifecycle on this shared bucket as a safety backstop for
# temporary mounted-storage files left behind by runtime use cases.
lifecycle_file="$(mktemp)"
trap 'rm -f "$lifecycle_file"' EXIT
cat > "$lifecycle_file" <<'EOF'
{
  "rule": [
    {
      "action": { "type": "Delete" },
      "condition": { "age": 3 }
    }
  ]
}
EOF
gcloud storage buckets update "gs://${BUCKET}" \
  --project="$PROJECT" \
  --lifecycle-file="$lifecycle_file"

# roles/storage.objectUser, not roles/storage.objectAdmin: this is Google's
# own documented role for a read-write Cloud Storage volume mount
# (https://docs.cloud.google.com/run/docs/configuring/services/cloud-storage-volume-mounts).
# Confirmed via `gcloud iam roles describe` that objectUser already includes
# everything a read-write mount needs (object create/get/delete/list/move/
# update, plus folders/managedFolders/multipartUploads for HNS-style buckets)
# and — the actual reason to prefer it over objectAdmin — excludes
# `storage.objects.getIamPolicy`/`setIamPolicy`/`setRetention`/
# `overrideUnlockedRetention`. Those are the only permissions objectAdmin
# adds on top of objectUser, and none of them are needed to read/write files
# through a mount: they let a holder change who else can access an object or
# override a retention lock, which only widens what a compromised worker
# process or leaked SA credential could do. A hand-rolled custom role here
# was considered and rejected: this bucket can be a shared runtime mount,
# and a custom role hand-copying a
# subset of a predefined role's permissions would silently drift out of sync
# if Cloud Run's GCS-mount feature (an evolving product) ever starts relying
# on a permission this list didn't anticipate — safer to track Google's own
# maintained role than to guess at a minimal permission set for an
# undocumented internal mechanism.
gcloud storage buckets add-iam-policy-binding "gs://${BUCKET}" \
  --member="serviceAccount:${RUNTIME_SA}" \
  --role="roles/storage.objectUser"
