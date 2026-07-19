#!/usr/bin/env bash
set -euo pipefail

# Initializes the Google Cloud and GitHub-side authentication needed by the
# operator-dispatched production release workflow.
#
# Expected defaults are set in .config/mise.toml. Run through mise or export
# the same variables manually:
#
#   mise exec -- ./build/init/gcp-github-release-auth.sh
#
# The script is intentionally idempotent for existing Google resources. IAM
# policy bindings and GitHub variables are safe to reapply.

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"

# shellcheck source=build/gcp-runtime-shared.sh
# shellcheck disable=SC1091
source "${ROOT_DIR}/build/gcp-runtime-shared.sh"

PROJECT_ID="${GCP_PROJECT_ID:-${PROJECT_ID}}"
REGION="${GCP_REGION:-${REGION}}"
ARTIFACT_REPOSITORY="${GCP_ARTIFACT_REPOSITORY:-zeeq}"
DEPLOYER_NAME="${GCP_RELEASE_DEPLOYER_NAME:-github-release-deployer}"
POOL_ID="${GCP_WORKLOAD_IDENTITY_POOL_ID:-github}"
PROVIDER_ID="${GCP_WORKLOAD_IDENTITY_PROVIDER_ID:-zeeq-app}"
GITHUB_REPO="${ZEEQ_GITHUB_REPO:-${GITHUB_REPOSITORY:-zeeq-ai/zeeq-app}}"
GH_ENVIRONMENT="${GH_RELEASE_ENVIRONMENT:-production}"
GH_VARIABLE_SCOPE="${GH_VARIABLE_SCOPE:-environment}"
RUNTIME_SA="${GCP_RUNTIME_SA:?Set GCP_RUNTIME_SA to the Cloud Run runtime service account email.}"

DEPLOYER_SA="${DEPLOYER_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command gcloud
require_command gh

PROJECT_NUMBER="$(gcloud projects describe "${PROJECT_ID}" --format='value(projectNumber)')"
POOL_RESOURCE="projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_ID}"
PROVIDER_RESOURCE="${POOL_RESOURCE}/providers/${PROVIDER_ID}"

echo "Initializing release deploy auth for ${GITHUB_REPO}"
echo "Google Cloud project: ${PROJECT_ID} (${PROJECT_NUMBER})"
echo "Runtime service account: ${RUNTIME_SA}"
echo "Deployer service account: ${DEPLOYER_SA}"

echo "Enabling required Google Cloud APIs..."
gcloud services enable \
  iam.googleapis.com \
  iamcredentials.googleapis.com \
  sts.googleapis.com \
  artifactregistry.googleapis.com \
  run.googleapis.com \
  secretmanager.googleapis.com \
  cloudresourcemanager.googleapis.com \
  --project="${PROJECT_ID}"

if gcloud iam service-accounts describe "${DEPLOYER_SA}" --project="${PROJECT_ID}" >/dev/null 2>&1; then
  echo "Service account already exists: ${DEPLOYER_SA}"
else
  echo "Creating service account: ${DEPLOYER_SA}"
  gcloud iam service-accounts create "${DEPLOYER_NAME}" \
    --project="${PROJECT_ID}" \
    --display-name="GitHub Actions Zeeq release deployer"
fi

if gcloud iam workload-identity-pools describe "${POOL_ID}" \
  --project="${PROJECT_ID}" \
  --location="global" >/dev/null 2>&1; then
  echo "Workload Identity Pool already exists: ${POOL_ID}"
else
  echo "Creating Workload Identity Pool: ${POOL_ID}"
  gcloud iam workload-identity-pools create "${POOL_ID}" \
    --project="${PROJECT_ID}" \
    --location="global" \
    --display-name="GitHub Actions Pool"
fi

if gcloud iam workload-identity-pools providers describe "${PROVIDER_ID}" \
  --project="${PROJECT_ID}" \
  --location="global" \
  --workload-identity-pool="${POOL_ID}" >/dev/null 2>&1; then
  echo "Workload Identity Provider already exists: ${PROVIDER_ID}"
else
  echo "Creating Workload Identity Provider: ${PROVIDER_ID}"
  gcloud iam workload-identity-pools providers create-oidc "${PROVIDER_ID}" \
    --project="${PROJECT_ID}" \
    --location="global" \
    --workload-identity-pool="${POOL_ID}" \
    --display-name="zeeq-app GitHub Actions" \
    --issuer-uri="https://token.actions.githubusercontent.com" \
    --attribute-mapping="google.subject=assertion.sub,attribute.actor=assertion.actor,attribute.repository=assertion.repository,attribute.repository_owner=assertion.repository_owner,attribute.ref=assertion.ref" \
    --attribute-condition="assertion.repository == '${GITHUB_REPO}' && assertion.ref == 'refs/heads/main'"
fi

echo "Granting GitHub Actions impersonation permission..."
gcloud iam service-accounts add-iam-policy-binding "${DEPLOYER_SA}" \
  --project="${PROJECT_ID}" \
  --role="roles/iam.workloadIdentityUser" \
  --member="principalSet://iam.googleapis.com/${POOL_RESOURCE}/attribute.repository/${GITHUB_REPO}"

echo "Granting deployer project permissions..."
gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
  --member="serviceAccount:${DEPLOYER_SA}" \
  --role="roles/run.admin"

gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
  --member="serviceAccount:${DEPLOYER_SA}" \
  --role="roles/artifactregistry.writer"

echo "Allowing deployer to attach the Cloud Run runtime service account..."
gcloud iam service-accounts add-iam-policy-binding "${RUNTIME_SA}" \
  --project="${PROJECT_ID}" \
  --member="serviceAccount:${DEPLOYER_SA}" \
  --role="roles/iam.serviceAccountUser"

if gcloud artifacts repositories describe "${ARTIFACT_REPOSITORY}" \
  --project="${PROJECT_ID}" \
  --location="${REGION}" >/dev/null 2>&1; then
  echo "Artifact Registry repository already exists: ${ARTIFACT_REPOSITORY}"
else
  echo "Creating Artifact Registry repository: ${ARTIFACT_REPOSITORY}"
  gcloud artifacts repositories create "${ARTIFACT_REPOSITORY}" \
    --project="${PROJECT_ID}" \
    --location="${REGION}" \
    --repository-format=docker \
    --description="Zeeq runtime images"
fi

echo "Granting runtime service account access to Secret Manager secrets..."
gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
  --member="serviceAccount:${RUNTIME_SA}" \
  --role="roles/secretmanager.secretAccessor"

echo "Setting GitHub Actions variables..."
if [[ "${GH_VARIABLE_SCOPE}" == "repo" ]]; then
  gh variable set GCP_WORKLOAD_IDENTITY_PROVIDER \
    --repo "${GITHUB_REPO}" \
    --body "${PROVIDER_RESOURCE}"
  gh variable set GCP_RELEASE_SERVICE_ACCOUNT \
    --repo "${GITHUB_REPO}" \
    --body "${DEPLOYER_SA}"
else
  gh variable set GCP_WORKLOAD_IDENTITY_PROVIDER \
    --repo "${GITHUB_REPO}" \
    --env "${GH_ENVIRONMENT}" \
    --body "${PROVIDER_RESOURCE}"
  gh variable set GCP_RELEASE_SERVICE_ACCOUNT \
    --repo "${GITHUB_REPO}" \
    --env "${GH_ENVIRONMENT}" \
    --body "${DEPLOYER_SA}"
fi

cat <<EOF

Release deploy authentication initialized.

GitHub variable scope: ${GH_VARIABLE_SCOPE}
GitHub environment: ${GH_ENVIRONMENT}
GCP_WORKLOAD_IDENTITY_PROVIDER=${PROVIDER_RESOURCE}
GCP_RELEASE_SERVICE_ACCOUNT=${DEPLOYER_SA}

IAM propagation can take several minutes before the first release run succeeds.
EOF
