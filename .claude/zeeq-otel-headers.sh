#!/usr/bin/env bash
set -euo pipefail

# See: https://code.claude.com/docs/en/monitoring-usage#dynamic-headers
: "${ZEEQ_OTEL_ACCESS_TOKEN:?Set ZEEQ_OTEL_ACCESS_TOKEN in your private environment before enabling telemetry}"
printf '{"Authorization":"Bearer %s"}\n' "$ZEEQ_OTEL_ACCESS_TOKEN"
