#!/usr/bin/env bash
set -euo pipefail

# Generate OpenIddict signing and encryption certificates for production.
#
# Output: .secrets/openiddict/signing.pfx and encryption.pfx
# These are X.509 certificates with RSA 4096-bit keys, valid for ~2.25 years.
#
# Usage:
#   ./build/certs/gen-openiddict-certs.sh
#
# You will be prompted for export passwords. Use strong, unique passwords and
# store them securely — they are required at runtime to load the private keys.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
SECRETS_DIR="$ROOT_DIR/.secrets/openiddict"

mkdir -p "$SECRETS_DIR"

echo "=== Generating OpenIddict signing certificate ==="
openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes \
  -subj "/CN=zeeq-openiddict signing" \
  -keyout "$SECRETS_DIR/signing.key" \
  -out "$SECRETS_DIR/signing.crt"

echo ""
echo "=== Exporting signing certificate to PFX ==="
echo "Enter a strong password for the SIGNING PFX (you'll need this at deploy time):"
openssl pkcs12 -export \
  -inkey "$SECRETS_DIR/signing.key" \
  -in "$SECRETS_DIR/signing.crt" \
  -out "$SECRETS_DIR/signing.pfx"

echo ""
echo "=== Generating OpenIddict encryption certificate ==="
openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes \
  -subj "/CN=zeeq-openiddict encryption" \
  -keyout "$SECRETS_DIR/encryption.key" \
  -out "$SECRETS_DIR/encryption.crt"

echo ""
echo "=== Exporting encryption certificate to PFX ==="
echo "Enter a strong password for the ENCRYPTION PFX (you'll need this at deploy time):"
openssl pkcs12 -export \
  -inkey "$SECRETS_DIR/encryption.key" \
  -in "$SECRETS_DIR/encryption.crt" \
  -out "$SECRETS_DIR/encryption.pfx"

echo ""
echo "=== Done ==="
echo "Certificates created in: $SECRETS_DIR"
echo ""
echo "Next steps:"
echo "  1. Set the signing password:  export ZEEQ_OPENIDDICT_SIGNING_PASSWORD='...'"
echo "  2. Set the encryption password: export ZEEQ_OPENIDDICT_ENCRYPTION_PASSWORD='...'"
echo "  3. Run: ./build/certs/upload-openiddict-secrets.sh"
