set -e

# Sets up Google Cloud KMS which is used for tenant secret encryption.

PROJECT=$GCP_PROJECT_ID # Set in .config/mise.toml
RUNTIME_SA=$GCP_RUNTIME_SA # Set via env var
REGION=us-central1
KEYRING=zeeq-runtime
KEY=zeeq-llm-secrets

gcloud services enable cloudkms.googleapis.com \
  --project="$PROJECT"

gcloud kms keyrings create "$KEYRING" \
  --location="$REGION" \
  --project="$PROJECT"

gcloud kms keys create "$KEY" \
  --keyring="$KEYRING" \
  --location="$REGION" \
  --purpose=encryption \
  --rotation-period=90d \
  --next-rotation-time="$(date -u -v+90d '+%Y-%m-%dT%H:%M:%SZ')" \
  --project="$PROJECT"

gcloud kms keys add-iam-policy-binding "$KEY" \
  --keyring="$KEYRING" \
  --location="$REGION" \
  --member="serviceAccount:${RUNTIME_SA}" \
  --role="roles/cloudkms.cryptoKeyEncrypterDecrypter" \
  --project="$PROJECT"
