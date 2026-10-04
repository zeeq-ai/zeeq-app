# syntax=docker/dockerfile:1-labs
ARG ZEEQ_BUILD_TARGET=gcp

# Multi-stage Dockerfile for Zeeq Server
#*----------------------------------------------------------------------------------
# Stage 1: Shared Yarn workspace install (used by web-build)
#*----------------------------------------------------------------------------------
FROM node:24 AS node-deps
WORKDIR /srcroot

# Copy only Yarn workspace manifests first to maximize install layer caching.
# Every workspace member's package.json must be present -- the "src/plugins/**"
# workspaces glob means yarn cannot see (and validate the lockfile against) a
# member whose manifest isn't copied yet, which makes an --immutable install
# fail as if the lockfile were out of date.
COPY ./package.json ./yarn.lock ./.yarnrc.yml ./
COPY ./src/web/package.json ./src/web/package.json
COPY --parents ./src/plugins/**/package.json ./

RUN corepack enable && yarn install --immutable

#*----------------------------------------------------------------------------------
# Stage 2: Build the Vue SPA
#*----------------------------------------------------------------------------------
FROM node-deps AS web-build

COPY ./src/web ./src/web
RUN VITE_BASE=/web/ yarn --cwd ./src/web build

#*----------------------------------------------------------------------------------
# Stage 3: Build the .NET Server
#*----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-gcp
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build-aws
FROM build-${ZEEQ_BUILD_TARGET} AS build
WORKDIR /srcroot
ARG ZEEQ_BUILD_TARGET=gcp
ARG BUILDARCH
# AWS builds on ARM hosts need a native compiler workaround for Grpc.Tools on 16KB pages.
# GCP builds retain the compiler supplied by the pinned Grpc.Tools package.
RUN case "$ZEEQ_BUILD_TARGET" in gcp|aws) ;; *) echo "Unsupported ZEEQ_BUILD_TARGET: $ZEEQ_BUILD_TARGET" >&2; exit 1 ;; esac && \
  if [ "$ZEEQ_BUILD_TARGET" = aws ] && [ "$BUILDARCH" = arm64 ]; then \
    apt-get update && apt-get install -y --no-install-recommends protobuf-compiler && rm -rf /var/lib/apt/lists/*; \
  fi

# See: https://docs.docker.com/engine/reference/builder/#automatic-platform-args-in-the-global-scope
ARG TARGETARCH
ARG GIT_SHA
ARG ZEEQ_VERSION
ARG ZEEQ_VERSION_TAG

# NOTE: Release builds must pass ZEEQ_VERSION and ZEEQ_VERSION_TAG. The args
# remain optional here so local/dev image builds can still report dev-* from SHA.

# Copy only the files needed for restore first (to leverage Docker layer caching)
# The .dockerignore file excludes bin/, obj/, tests, and other non-essential files.
COPY --parents ./src/backend/Directory.* ./
COPY --parents ./src/backend/**/*.csproj ./

# Restore dependencies
COPY nuget.config ./
RUN dotnet restore ./src/backend/Zeeq.Runtime.Server/Zeeq.Runtime.Server.csproj -a $TARGETARCH --verbosity normal

# Copy the full backend source tree (core, integrations, mcp, platform, runtime)
COPY ./src/backend ./src/backend

# Copy the Vue SPA build output into the server's wwwroot
COPY --from=web-build /srcroot/src/web/dist ./src/backend/Zeeq.Runtime.Server/wwwroot/web

# Build the application without restore (already restored above)
RUN if [ "$ZEEQ_BUILD_TARGET" = aws ] && [ "$BUILDARCH" = arm64 ]; then export PROTOBUF_PROTOC=/usr/bin/protoc; fi && \
  dotnet publish ./src/backend/Zeeq.Runtime.Server/Zeeq.Runtime.Server.csproj \
  --configuration Release \
  --no-restore \
  --output /app/publish \
  -a $TARGETARCH \
  -p:GIT_SHA=$GIT_SHA \
  -p:ZEEQ_VERSION=$ZEEQ_VERSION \
  -p:ZEEQ_VERSION_TAG=$ZEEQ_VERSION_TAG

#*----------------------------------------------------------------------------------
# Stage 4: Build the .NET Runtime Image
#*----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG ZEEQ_BUILD_TARGET=gcp
ARG GIT_SHA
ARG ZEEQ_VERSION
ARG ZEEQ_VERSION_TAG

LABEL org.opencontainers.image.source="https://github.com/zeeq-ai/zeeq-app"
LABEL org.opencontainers.image.revision="${GIT_SHA}"
LABEL org.opencontainers.image.version="${ZEEQ_VERSION}"
LABEL ai.zeeq.version-tag="${ZEEQ_VERSION_TAG}"

# Install git and GitHub CLI
RUN apt-get update && \
  apt-get install -y --no-install-recommends \
  git \
  curl \
  ca-certificates && \
  curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg && \
  chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg && \
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | tee /etc/apt/sources.list.d/github-cli.list > /dev/null && \
  apt-get update && \
  apt-get install -y --no-install-recommends gh && \
  apt-get clean && \
  rm -rf /var/lib/apt/lists/*

# Only AWS images need the RDS trust bundle; GCP builds do not contact this endpoint.
RUN case "$ZEEQ_BUILD_TARGET" in \
    aws) curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /app/rds-ca.pem ;; \
    gcp) ;; \
    *) echo "Unsupported ZEEQ_BUILD_TARGET: $ZEEQ_BUILD_TARGET" >&2; exit 1 ;; \
  esac
COPY --from=build /app/publish .

# 8080: web/API, 8081: OTLP/gRPC ingest, 8082: OTLP/HTTP ingest.
EXPOSE 8080 8081 8082

# Fallback for non-production/default config. Production config defines explicit
# Kestrel endpoints for all exposed ports.
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Zeeq.Runtime.Server.dll"]
