# syntax=docker/dockerfile:1

# Zeeq uses the contrib collector distribution because the production config
# requires the OIDC authenticator, headers_setter extension, filter processor,
# memory_limiter processor, batch processor, and otlphttp exporter.
#
# The first production rollout intentionally pins the version already exercised
# by local Aspire. Test collector upgrades as separate changes because collector
# component syntax and behavior can change between releases.
ARG OTEL_COLLECTOR_BASE_IMAGE=otel/opentelemetry-collector-contrib:0.153.0

FROM ${OTEL_COLLECTOR_BASE_IMAGE}

# This image packages only the production collector config. Runtime secrets and
# environment-specific values are injected by the VM startup script.
COPY build/otel-collector-config.gcp.yaml /etc/otel-collector-config.yaml

# The upstream image provides the collector binary and entrypoint. Override only
# the config path so deployment scripts can run this image directly.
CMD ["--config=/etc/otel-collector-config.yaml"]
