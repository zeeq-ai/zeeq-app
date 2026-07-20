#!/usr/bin/env bash
# shellcheck disable=SC2034

# Shared Cloud Run deployment settings for the web service and worker pool.
# Callers can override the defaults by setting GCP_PROJECT_ID or GCP_REGION.
PROJECT_ID="${GCP_PROJECT_ID:-zeeq-ai-prod}"
REGION="${GCP_REGION:-us-central1}"

# The web service and worker pool run the same published container image and
# attach to the same Cloud SQL instance.
ZEEQ_RUNTIME_IMAGE="${ZEEQ_RUNTIME_IMAGE:-${REGION}-docker.pkg.dev/${PROJECT_ID}/zeeq/zeeq-runtime:latest}"
ZEEQ_CLOUDSQL_INSTANCE="${PROJECT_ID}:${REGION}:zeeq-pg-prod"

# Worker-only ingest workspace volume. Cloud Run ephemeral disk currently has
# a 10Gi minimum, so keep the default explicit instead of implying 2.5Gi works.
ZEEQ_INGEST_MOUNT_PATH="/mnt/ingest"
ZEEQ_INGEST_EPHEMERAL_DISK_SIZE="${ZEEQ_INGEST_EPHEMERAL_DISK_SIZE:-10Gi}"

# OpenIddict only runs in the HTTP web runtime. These are file paths inside the
# web container after Cloud Run mounts the Secret Manager certificate payloads.
ZEEQ_OPENIDDICT_SIGNING_CERTIFICATE_PATH="/run/secrets/openiddict/signing/signing.pfx"
ZEEQ_OPENIDDICT_ENCRYPTION_CERTIFICATE_PATH="/run/secrets/openiddict/encryption/encryption.pfx"

# Telemetry endpoint
OTEL_EXPORTER_OTLP_ENDPOINT="https://api.honeycomb.io:443"

# Joins array elements with commas for gcloud's comma-delimited list flags.
join_by_comma() {
  local IFS=,
  echo "$*"
}

# Secret Manager values injected as environment variables, as KEY=SECRET_NAME:VERSION.
# These can be used by both Cloud Run services and worker pools.
ZEEQ_RUNTIME_ENV_SECRETS=(
  "Auth__OpenIddict__SigningCertificatePassword=zeeq-openiddict-signing-password:latest"
  "Auth__OpenIddict__EncryptionCertificatePassword=zeeq-openiddict-encryption-password:latest"
  "AppSettings__GitHub__PrivateKeyPem=AppSettings__GitHub__PrivateKeyPem:latest"
  "AppSettings__GitHub__WebhookSecret=AppSettings__GitHub__WebhookSecret:latest"
  "AppSettings__CodeReview__ReviewRequestLinkEncryptionKey=AppSettings__CodeReview__ReviewRequestLinkEncryptionKey:latest"
  "AppSettings__Documents__LibraryExportSigningKey=AppSettings__Documents__LibraryExportSigningKey:latest"
  "AppSettings__Platform__SystemAdminSubjects__0=AppSettings__Platform__SystemAdminSubjects__0:latest"
  "AppSettings__Llm__Models__Fast__ApiKey=AppSettings__Llm__Models__Fast__ApiKey:latest"
  "AppSettings__Llm__Embeddings__ApiKey=AppSettings__Llm__Embeddings__ApiKey:latest"
  "AppSettings__Auth__Providers__0__ClientSecret=AppSettings__Auth__Providers__0__ClientSecret:latest"
  "AppSettings__Auth__Providers__1__ClientSecret=AppSettings__Auth__Providers__1__ClientSecret:latest"
  "AppSettings__Database__ConnectionString=AppSettings__Database__ConnectionString:latest"
  "OTEL_EXPORTER_OTLP_HEADERS=zeeq-otel-api-key-header:latest"
)

# Secret Manager values mounted as files. Cloud Run worker pools do not support
# secret volume mounts, so keep file-mounted secrets out of worker deployments.
ZEEQ_RUNTIME_CERTIFICATE_VOLUME_SECRETS=(
  "${ZEEQ_OPENIDDICT_SIGNING_CERTIFICATE_PATH}=zeeq-openiddict-signing-pfx:latest"
  "${ZEEQ_OPENIDDICT_ENCRYPTION_CERTIFICATE_PATH}=zeeq-openiddict-encryption-pfx:latest"
)

# The web runtime needs both normal env secrets and mounted OpenIddict
# certificates because it hosts ASP.NET Core and token issuance.
ZEEQ_RUNTIME_WEB_SECRETS=(
  "${ZEEQ_RUNTIME_CERTIFICATE_VOLUME_SECRETS[@]}"
  "${ZEEQ_RUNTIME_ENV_SECRETS[@]}"
)

# The worker runtime exits into the generic host before web/auth setup, so it
# must not request the OpenIddict certificate volume mounts.
ZEEQ_RUNTIME_WORKER_SECRETS=(
  "${ZEEQ_RUNTIME_ENV_SECRETS[@]}"
)

# Plain environment values shared by both runtimes. Role-specific settings such
# as ZEEQ_RUN_MODE and ZEEQ_MESSAGING_ROLE stay in the caller scripts.
ZEEQ_RUNTIME_COMMON_ENV_VARS=(
  "ASPNETCORE_ENVIRONMENT=Production"
  "DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=false"
  "GCP_PROJECT_ID=${PROJECT_ID}"
  "ZeeqMessaging__Provider=GcpPubSub"
  "ZeeqMessaging__GcpPubSub__ProjectId=${PROJECT_ID}"
  "OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf"
  "OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT}"
)

# Web-only OpenIddict certificate path settings. These point at mounted files,
# so do not include them in the worker pool env vars.
ZEEQ_RUNTIME_OPENIDDICT_CERTIFICATE_ENV_VARS=(
  "Auth__OpenIddict__SigningCertificatePath=${ZEEQ_OPENIDDICT_SIGNING_CERTIFICATE_PATH}"
  "Auth__OpenIddict__EncryptionCertificatePath=${ZEEQ_OPENIDDICT_ENCRYPTION_CERTIFICATE_PATH}"
)
