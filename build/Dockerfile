# syntax=docker/dockerfile:1-labs

# Multi-stage Dockerfile for Zeeq Server
#*----------------------------------------------------------------------------------
# Stage 1: Shared Yarn workspace install (used by web-build)
#*----------------------------------------------------------------------------------
FROM node:24 AS node-deps
WORKDIR /srcroot

# Copy only Yarn workspace manifests first to maximize install layer caching.
COPY ./package.json ./yarn.lock ./.yarnrc.yml ./
COPY ./src/web/package.json ./src/web/package.json

RUN corepack enable && yarn install --immutable

#*----------------------------------------------------------------------------------
# Stage 2: Build the Vue SPA
#*----------------------------------------------------------------------------------
FROM node-deps AS web-build

COPY ./src/web ./src/web
RUN yarn --cwd ./src/web generate
RUN VITE_BASE=/web/ yarn --cwd ./src/web build

#*----------------------------------------------------------------------------------
# Stage 3: Build the .NET Server
#*----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /srcroot

# See: https://docs.docker.com/engine/reference/builder/#automatic-platform-args-in-the-global-scope
ARG TARGETARCH
ARG GIT_SHA

# Copy only the files needed for restore first (to leverage Docker layer caching)
# The .dockerignore file excludes bin/, obj/, tests, and other non-essential files.
COPY --parents ./src/backend/Directory.* ./
COPY --parents ./src/backend/**/*.csproj ./

# Restore dependencies
RUN dotnet restore ./src/backend/Zeeq.Runtime.Server/Zeeq.Runtime.Server.csproj -a $TARGETARCH

# Copy the full backend source tree (core, integrations, mcp, platform, runtime)
COPY ./src/backend ./src/backend

# Copy the Vue SPA build output into the server's wwwroot
COPY --from=web-build /srcroot/src/web/dist ./src/backend/Zeeq.Runtime.Server/wwwroot/web

# Build the application without restore (already restored above)
RUN dotnet publish ./src/backend/Zeeq.Runtime.Server/Zeeq.Runtime.Server.csproj \
  --configuration Release \
  --no-restore \
  --output /app/publish \
  -a $TARGETARCH \
  -p:GIT_SHA=$GIT_SHA

#*----------------------------------------------------------------------------------
# Stage 4: Build the .NET Runtime Image
#*----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

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

COPY --from=build /app/publish .

# 8080: web/API, 8081: OTLP/gRPC ingest, 8082: OTLP/HTTP ingest.
EXPOSE 8080 8081 8082

# Fallback for non-production/default config. Production config defines explicit
# Kestrel endpoints for all exposed ports.
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Zeeq.Runtime.Server.dll"]
