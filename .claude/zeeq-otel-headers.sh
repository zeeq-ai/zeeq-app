#!/usr/bin/env bash

# See: https://code.claude.com/docs/en/monitoring-usage#dynamic-headers
printf '{"Authorization":"Bearer %s"}\n' "$OPEN_ZEEQ_ACCESS_TOKEN_LOCAL"
