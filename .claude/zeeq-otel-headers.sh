#!/usr/bin/env bash

# See: https://code.claude.com/docs/en/monitoring-usage#dynamic-headers
printf '{"Authorization":"Bearer %s"}\n' "$ZEEQ_OTEL_ACCESS_TOKEN"
