#!/usr/bin/env bash
set -euo pipefail
umask 077

# Print OpenIddict settings for build/aws/application.local.json.
# Certificate files are temporary; the printed JSON values contain private keys.
command -v openssl >/dev/null || { echo 'OpenSSL is required.' >&2; exit 1; }
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/zeeq-aws-certs.XXXXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

generate_certificate() {
  local purpose="$1"
  openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes \
    -subj "/CN=zeeq-openiddict $purpose" \
    -keyout "$WORK_DIR/$purpose.key" \
    -out "$WORK_DIR/$purpose.crt" 2>"$WORK_DIR/openssl.log" || {
      echo "Failed to generate the $purpose certificate." >&2
      exit 1
    }
  openssl pkcs12 -export \
    -inkey "$WORK_DIR/$purpose.key" \
    -in "$WORK_DIR/$purpose.crt" \
    -out "$WORK_DIR/$purpose.pfx" \
    -passout env:ZEEQ_CERT_PASSWORD
}

print_value() {
  local step="$1" field="$2" value="$3"
  printf '\nStep %s of 4: Copy this value into AppSettings.Auth.OpenIddict.%s\n\n' "$step" "$field"
  printf '"%s": "%s"\n' "$field" "$value"
  if [[ "$step" != 4 ]]; then
    printf '\nPress Enter after copying to continue: '
    IFS= read -r _
  fi
}

printf 'Generate new OpenIddict certificates for application.local.json.\n'
printf 'The following values are secrets. Store them only in the private settings file.\n'
printf 'Keep these values for later deployments; rerunning creates different keys.\n'

export ZEEQ_CERT_PASSWORD
ZEEQ_CERT_PASSWORD="$(openssl rand -base64 32)"
generate_certificate signing
print_value 1 SigningCertificateBase64 "$(openssl base64 -A -in "$WORK_DIR/signing.pfx")"
print_value 2 SigningCertificatePassword "$ZEEQ_CERT_PASSWORD"

ZEEQ_CERT_PASSWORD="$(openssl rand -base64 32)"
generate_certificate encryption
print_value 3 EncryptionCertificateBase64 "$(openssl base64 -A -in "$WORK_DIR/encryption.pfx")"
print_value 4 EncryptionCertificatePassword "$ZEEQ_CERT_PASSWORD"
unset ZEEQ_CERT_PASSWORD

printf '\nAll four values belong in AppSettings.Auth.OpenIddict in application.local.json.\n'
printf 'Temporary certificate files are removed when this script exits.\n'
