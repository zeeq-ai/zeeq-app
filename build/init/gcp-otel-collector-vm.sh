#!/usr/bin/env bash
set -euo pipefail

# Initializes the standalone production OTEL collector VM on Google Compute
# Engine. The script is intentionally idempotent for existing resources.
#
# Shape:
#   public https://otel.zeeq.ai:443 -> Caddy -> private collector network port 4318
#
# DNS is managed at the registrar/DNS provider, not by this script. The script
# reserves and prints the static IP so an operator can create the required A
# record for the collector hostname.

PROJECT_ID="${GCP_PROJECT_ID:-zeeq-ai-prod}"
REGION="${GCP_REGION:-us-central1}"
ZONE="${GCP_ZONE:-us-central1-a}"
ARTIFACT_REPOSITORY="${GCP_ARTIFACT_REPOSITORY:-zeeq}"
INSTANCE_NAME="${ZEEQ_OTEL_COLLECTOR_VM_NAME:-zeeq-otel-collector}"
MACHINE_TYPE="${ZEEQ_OTEL_COLLECTOR_MACHINE_TYPE:-e2-small}"
NETWORK="${GCP_NETWORK:-default}"
SUBNET="${GCP_SUBNET:-default}"
ADDRESS_NAME="${ZEEQ_OTEL_COLLECTOR_ADDRESS_NAME:-zeeq-otel-collector-ip}"
SERVICE_ACCOUNT_NAME="${ZEEQ_OTEL_COLLECTOR_SA_NAME:-zeeq-otel-collector}"
DOMAIN="${ZEEQ_OTEL_COLLECTOR_DOMAIN:-otel.zeeq.ai}"
NETWORK_TAG="${ZEEQ_OTEL_COLLECTOR_NETWORK_TAG:-zeeq-otel-collector}"
FIREWALL_RULE="${ZEEQ_OTEL_COLLECTOR_FIREWALL_RULE:-allow-zeeq-otel-collector-https}"
IMAGE="${ZEEQ_OTEL_COLLECTOR_IMAGE:-${REGION}-docker.pkg.dev/${PROJECT_ID}/${ARTIFACT_REPOSITORY}/zeeq-otel-collector:latest}"
ZEEQ_ISSUER_URL="${ZEEQ_OTEL_COLLECTOR_ISSUER_URL:-${ZEEQ_ISSUER_URL:-https://app.zeeq.ai/}}"
ZEEQ_OTLP_HTTP_ENDPOINT="${ZEEQ_OTEL_COLLECTOR_OTLP_HTTP_ENDPOINT:-${ZEEQ_OTLP_HTTP_ENDPOINT:-https://app.zeeq.ai}}"
ZEEQ_TELEMETRY_AUDIENCE="${ZEEQ_OTEL_COLLECTOR_TELEMETRY_AUDIENCE:-${ZEEQ_TELEMETRY_AUDIENCE:-https://app.zeeq.ai/mcp}}"
CADDY_IMAGE="${ZEEQ_OTEL_COLLECTOR_CADDY_IMAGE:-caddy:2}"
CADDY_ACME_EMAIL="${CADDY_ACME_EMAIL:-}"

SERVICE_ACCOUNT_EMAIL="${SERVICE_ACCOUNT_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command gcloud

add_project_iam_policy_binding_with_retry() {
  local member="$1"
  local role="$2"
  local attempt

  for attempt in {1..10}; do
    if gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
      --member="${member}" \
      --role="${role}" \
      --condition=None \
      --quiet >/dev/null; then
      return 0
    fi

    # Newly created service accounts can take a short time to propagate to the
    # IAM backend used by project policy binding validation. Retrying keeps this
    # first-run script idempotent instead of requiring a manual second run.
    echo "IAM binding for ${member} -> ${role} failed; retrying after propagation delay (${attempt}/10)..." >&2
    sleep 6
  done

  echo "Failed to grant ${role} to ${member} after retries." >&2
  return 1
}

if [[ -z "$DOMAIN" ]]; then
  echo "ZEEQ_OTEL_COLLECTOR_DOMAIN must not be empty." >&2
  exit 1
fi

echo "Initializing Zeeq OTEL collector VM"
echo "Project: ${PROJECT_ID}"
echo "Region: ${REGION}"
echo "Zone: ${ZONE}"
echo "Instance: ${INSTANCE_NAME}"
echo "Domain: ${DOMAIN}"
echo "Collector image: ${IMAGE}"

echo "Enabling required Google Cloud APIs..."
gcloud services enable \
  compute.googleapis.com \
  artifactregistry.googleapis.com \
  iam.googleapis.com \
  logging.googleapis.com \
  monitoring.googleapis.com \
  --project="${PROJECT_ID}"

if gcloud iam service-accounts describe "${SERVICE_ACCOUNT_EMAIL}" --project="${PROJECT_ID}" >/dev/null 2>&1; then
  echo "Service account already exists: ${SERVICE_ACCOUNT_EMAIL}"
else
  echo "Creating service account: ${SERVICE_ACCOUNT_EMAIL}"
  gcloud iam service-accounts create "${SERVICE_ACCOUNT_NAME}" \
    --project="${PROJECT_ID}" \
    --display-name="Zeeq OTEL collector VM"
fi

echo "Granting VM service account read/logging/monitoring permissions..."
for role in roles/artifactregistry.reader roles/logging.logWriter roles/monitoring.metricWriter; do
  add_project_iam_policy_binding_with_retry "serviceAccount:${SERVICE_ACCOUNT_EMAIL}" "${role}"
done

if gcloud artifacts repositories describe "${ARTIFACT_REPOSITORY}" \
  --project="${PROJECT_ID}" \
  --location="${REGION}" >/dev/null 2>&1; then
  echo "Artifact Registry repository already exists: ${ARTIFACT_REPOSITORY}"
else
  echo "Creating Artifact Registry Docker repository: ${ARTIFACT_REPOSITORY}"
  gcloud artifacts repositories create "${ARTIFACT_REPOSITORY}" \
    --project="${PROJECT_ID}" \
    --location="${REGION}" \
    --repository-format=docker \
    --description="Zeeq runtime and collector images"
fi

if gcloud compute addresses describe "${ADDRESS_NAME}" \
  --project="${PROJECT_ID}" \
  --region="${REGION}" >/dev/null 2>&1; then
  echo "Static IP already reserved: ${ADDRESS_NAME}"
else
  echo "Reserving regional static IPv4 address: ${ADDRESS_NAME}"
  gcloud compute addresses create "${ADDRESS_NAME}" \
    --project="${PROJECT_ID}" \
    --region="${REGION}" \
    --network-tier=PREMIUM
fi

STATIC_IP="$(gcloud compute addresses describe "${ADDRESS_NAME}" \
  --project="${PROJECT_ID}" \
  --region="${REGION}" \
  --format='value(address)')"

echo "Reserved static IP: ${STATIC_IP}"
echo "Required DNS A record: ${DOMAIN} -> ${STATIC_IP}"
echo "DNS records do not include ports; clients use https://${DOMAIN} on 443."

if command -v dig >/dev/null 2>&1; then
  DNS_IP="$(dig +short "${DOMAIN}" A | tail -n 1 || true)"
  if [[ -n "$DNS_IP" ]]; then
    echo "Current DNS A lookup for ${DOMAIN}: ${DNS_IP}"
    if [[ "$DNS_IP" != "$STATIC_IP" ]]; then
      echo "WARNING: DNS does not currently point at the reserved static IP." >&2
    fi
  else
    echo "No current DNS A record observed for ${DOMAIN}."
  fi
else
  echo "dig is not installed locally; skipping DNS lookup preflight."
fi

if gcloud compute firewall-rules describe "${FIREWALL_RULE}" \
  --project="${PROJECT_ID}" >/dev/null 2>&1; then
  echo "Firewall rule already exists: ${FIREWALL_RULE}"
else
  echo "Creating firewall rule ${FIREWALL_RULE} for public HTTP/HTTPS only"
  gcloud compute firewall-rules create "${FIREWALL_RULE}" \
    --project="${PROJECT_ID}" \
    --network="${NETWORK}" \
    --direction=INGRESS \
    --priority=1000 \
    --action=ALLOW \
    --rules=tcp:80,tcp:443 \
    --source-ranges=0.0.0.0/0 \
    --target-tags="${NETWORK_TAG}" \
    --description="Allow public HTTP/HTTPS to Zeeq OTEL collector Caddy proxy"
fi

STARTUP_SCRIPT="$(mktemp)"
trap 'rm -f "${STARTUP_SCRIPT}"' EXIT

cat >"${STARTUP_SCRIPT}" <<EOF
#!/usr/bin/env bash
set -euo pipefail

# Generated by build/init/gcp-otel-collector-vm.sh.
# This script runs on Container-Optimized OS at boot and may be re-run manually.
# It restarts containers but preserves Caddy's /data and /config directories,
# which hold ACME account data, certificates, private keys, OCSP state, and
# autosaved config.

REGION="${REGION}"
DOMAIN="${DOMAIN}"
IMAGE="${IMAGE}"
CADDY_IMAGE="${CADDY_IMAGE}"
CADDY_ACME_EMAIL="${CADDY_ACME_EMAIL}"
ZEEQ_ISSUER_URL="${ZEEQ_ISSUER_URL}"
ZEEQ_TELEMETRY_AUDIENCE="${ZEEQ_TELEMETRY_AUDIENCE}"
ZEEQ_OTLP_HTTP_ENDPOINT="${ZEEQ_OTLP_HTTP_ENDPOINT}"

mkdir -p /etc/zeeq-otel-collector
mkdir -p /var/lib/zeeq-caddy/data
mkdir -p /var/lib/zeeq-caddy/config
mkdir -p /var/lib/zeeq-docker-config

if [[ -n "\${CADDY_ACME_EMAIL}" ]]; then
  cat >/etc/zeeq-otel-collector/Caddyfile <<CADDY
{
	email \${CADDY_ACME_EMAIL}
}

\${DOMAIN} {
	# Public OTLP/HTTP arrives over normal HTTPS on 443. The collector is on
	# the private Docker network and does not publish port 4318 to the VM.
	reverse_proxy zeeq-otel-collector:4318
}
CADDY
else
  cat >/etc/zeeq-otel-collector/Caddyfile <<CADDY
\${DOMAIN} {
	# Public OTLP/HTTP arrives over normal HTTPS on 443. The collector is on
	# the private Docker network and does not publish port 4318 to the VM.
	reverse_proxy zeeq-otel-collector:4318
}
CADDY
fi

# Configure Docker credential helper for Artifact Registry when available on
# COS. Root's home is read-only on Container-Optimized OS, so keep Docker's
# config.json under /var/lib instead of the default /root/.docker path.
if command -v docker-credential-gcr >/dev/null 2>&1; then
  DOCKER_CONFIG=/var/lib/zeeq-docker-config docker-credential-gcr configure-docker --registries="\${REGION}-docker.pkg.dev" >/dev/null
fi

DOCKER_CONFIG=/var/lib/zeeq-docker-config docker pull "\${IMAGE}"
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker pull "\${CADDY_IMAGE}"

# Caddy and the collector share a private Docker network. Caddy publishes
# 80/443 to the VM; the collector publishes nothing, so 4318 stays private.
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker network create zeeq-otel >/dev/null 2>&1 || true
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker rm -f zeeq-otel-collector >/dev/null 2>&1 || true
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker run -d \
  --name zeeq-otel-collector \
  --restart unless-stopped \
  --network zeeq-otel \
  -e ZEEQ_ISSUER_URL="\${ZEEQ_ISSUER_URL}" \
  -e ZEEQ_TELEMETRY_AUDIENCE="\${ZEEQ_TELEMETRY_AUDIENCE}" \
  -e ZEEQ_OTLP_HTTP_ENDPOINT="\${ZEEQ_OTLP_HTTP_ENDPOINT}" \
  "\${IMAGE}"

# Caddy owns public 80/443, obtains and renews certificates automatically, and
# proxies to the private collector listener. Use explicit port publishing rather
# than host networking on COS; host networking made Caddy reachable on loopback
# but not reliably reachable through the VM's external NAT address. /data is
# durable TLS state and must not be deleted as a cache.
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker rm -f zeeq-otel-caddy >/dev/null 2>&1 || true
DOCKER_CONFIG=/var/lib/zeeq-docker-config docker run -d \
  --name zeeq-otel-caddy \
  --restart unless-stopped \
  --network zeeq-otel \
  -p 80:80 \
  -p 443:443 \
  -e CADDY_ACME_EMAIL="\${CADDY_ACME_EMAIL}" \
  -e ZEEQ_OTEL_COLLECTOR_DOMAIN="\${DOMAIN}" \
  -v /etc/zeeq-otel-collector/Caddyfile:/etc/caddy/Caddyfile:ro \
  -v /var/lib/zeeq-caddy/data:/data \
  -v /var/lib/zeeq-caddy/config:/config \
  "\${CADDY_IMAGE}"

DOCKER_CONFIG=/var/lib/zeeq-docker-config docker ps --filter name=zeeq-otel --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
EOF

INSTANCE_EXISTS=false
if gcloud compute instances describe "${INSTANCE_NAME}" \
  --project="${PROJECT_ID}" \
  --zone="${ZONE}" >/dev/null 2>&1; then
  INSTANCE_EXISTS=true
fi

if [[ "$INSTANCE_EXISTS" == "true" ]]; then
  echo "VM already exists: ${INSTANCE_NAME}"
  CURRENT_VM_IP="$(gcloud compute instances describe "${INSTANCE_NAME}" \
    --project="${PROJECT_ID}" \
    --zone="${ZONE}" \
    --format='value(networkInterfaces[0].accessConfigs[0].natIP)')"

  if [[ "$CURRENT_VM_IP" != "$STATIC_IP" ]]; then
    cat >&2 <<ERROR
Existing VM external IP does not match the reserved collector static IP.

Instance: ${INSTANCE_NAME}
Current VM IP: ${CURRENT_VM_IP:-<none>}
Reserved static IP: ${STATIC_IP}

This script will not silently rebind the VM network interface because that can
interrupt production traffic. Attach the reserved static IP deliberately, or
delete/recreate the collector VM through an explicit operator-approved change.
ERROR
    exit 1
  fi

  echo "Updating startup-script metadata and running it once over SSH."
  gcloud compute instances add-metadata "${INSTANCE_NAME}" \
    --project="${PROJECT_ID}" \
    --zone="${ZONE}" \
    --metadata-from-file startup-script="${STARTUP_SCRIPT}"

  gcloud compute ssh "${INSTANCE_NAME}" \
    --project="${PROJECT_ID}" \
    --zone="${ZONE}" \
    --command='sudo google_metadata_script_runner startup'
else
  echo "Creating Container-Optimized OS VM: ${INSTANCE_NAME}"
  gcloud compute instances create "${INSTANCE_NAME}" \
    --project="${PROJECT_ID}" \
    --zone="${ZONE}" \
    --machine-type="${MACHINE_TYPE}" \
    --image-family=cos-stable \
    --image-project=cos-cloud \
    --boot-disk-size=20GB \
    --boot-disk-type=pd-balanced \
    --address="${STATIC_IP}" \
    --network="${NETWORK}" \
    --subnet="${SUBNET}" \
    --tags="${NETWORK_TAG}" \
    --service-account="${SERVICE_ACCOUNT_EMAIL}" \
    --scopes=https://www.googleapis.com/auth/cloud-platform \
    --metadata-from-file startup-script="${STARTUP_SCRIPT}"
fi

cat <<SUMMARY

Zeeq OTEL collector VM setup complete.

Instance: ${INSTANCE_NAME}
Static IP: ${STATIC_IP}
DNS A record required: ${DOMAIN} -> ${STATIC_IP}

Caddy needs public tcp:80 and tcp:443 for automatic HTTPS. If the DNS zone uses
CAA records, ensure letsencrypt.org is allowed. DNS does not carry ports; agent
harnesses should use https://${DOMAIN}, while Caddy forwards privately to
the collector container on the private Docker network.
SUMMARY
