#!/usr/bin/env bash
set -euo pipefail

# Upload OpenIddict PFX certificates and passwords to Google Secret Manager.
#
# Prerequisites:
#   - Certificates generated with ./build/certs/gen-openiddict-certs.sh
#   - Passwords exported as environment variables:
#       ZEEQ_OPENIDDICT_SIGNING_PASSWORD
#       ZEEQ_OPENIDDICT_ENCRYPTION_PASSWORD
#   - gcloud authenticated with zeeq-mcp-prod project access
#
# Usage:
#   export ZEEQ_OPENIDDICT_SIGNING_PASSWORD='...'
#   export ZEEQ_OPENIDDICT_ENCRYPTION_PASSWORD='...'
#   ./build/certs/upload-openiddict-secrets.sh

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
SECRETS_DIR="$ROOT_DIR/.secrets/openiddict"
PROJECT=$GCP_PROJECT_ID

: "${ZEEQ_OPENIDDICT_SIGNING_PASSWORD:?Must set ZEEQ_OPENIDDICT_SIGNING_PASSWORD}"
: "${ZEEQ_OPENIDDICT_ENCRYPTION_PASSWORD:?Must set ZEEQ_OPENIDDICT_ENCRYPTION_PASSWORD}"

if [[ ! -f "$SECRETS_DIR/signing.pfx" ]]; then
  echo "ERROR: signing.pfx not found. Run ./build/certs/gen-openiddict-certs.sh first."
  exit 1
fi

if [[ ! -f "$SECRETS_DIR/encryption.pfx" ]]; then
  echo "ERROR: encryption.pfx not found. Run ./build/certs/gen-openiddict-certs.sh first."
  exit 1
fi

echo "=== Uploading signing certificate to Secret Manager ==="
gcloud secrets create zeeq-openiddict-signing-pfx \
  --replication-policy=automatic \
  --data-file="$SECRETS_DIR/signing.pfx" \
  --project="$PROJECT" 2>/dev/null || \
gcloud secrets versions add zeeq-openiddict-signing-pfx \
  --data-file="$SECRETS_DIR/signing.pfx" \
  --project="$PROJECT"

echo "=== Uploading encryption certificate to Secret Manager ==="
gcloud secrets create zeeq-openiddict-encryption-pfx \
  --replication-policy=automatic \
  --data-file="$SECRETS_DIR/encryption.pfx" \
  --project="$PROJECT" 2>/dev/null || \
gcloud secrets versions add zeeq-openiddict-encryption-pfx \
  --data-file="$SECRETS_DIR/encryption.pfx" \
  --project="$PROJECT"

echo "=== Uploading signing password to Secret Manager ==="
echo -n "$ZEEQ_OPENIDDICT_SIGNING_PASSWORD" | \
gcloud secrets create zeeq-openiddict-signing-password \
  --replication-policy=automatic \
  --data-file=- \
  --project="$PROJECT" 2>/dev/null || \
echo -n "$ZEEQ_OPENIDDICT_SIGNING_PASSWORD" | \
gcloud secrets versions add zeeq-openiddict-signing-password \
  --data-file=- \
  --project="$PROJECT"

echo "=== Uploading encryption password to Secret Manager ==="
echo -n "$ZEEQ_OPENIDDICT_ENCRYPTION_PASSWORD" | \
gcloud secrets create zeeq-openiddict-encryption-password \
  --replication-policy=automatic \
  --data-file=- \
  --project="$PROJECT" 2>/dev/null || \
gcloud secrets versions add zeeq-openiddict-encryption-password \
  --data-file=- \
  --project="$PROJECT"

echo ""
echo "=== Done ==="
echo "Secrets uploaded to project: $PROJECT"
echo ""
echo "Now run: ./build/ship.sh"
