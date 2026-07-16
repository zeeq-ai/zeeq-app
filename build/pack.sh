set -e

PROJECT_ID="${GCP_PROJECT_ID:-zeeq-ai-prod}"
REGION="${GCP_REGION:-us-central1}"

# Run from the root; local build-ship for now

# Build the binaries as a check before Docker build
dotnet build zeeq.slnx

# Build the static assets as a check before Docker build
VITE_BASE=/web/ yarn --cwd ./src/web build
NUXT_APP_BASE_URL=/docs/ yarn --cwd ./docs generate

# Run the tests as a smoke check
dotnet test --no-build --no-restore

GIT_SHA=$(git rev-parse HEAD)

# Build and push the container
docker buildx build \
  --push \
  --platform linux/amd64 \
  --build-arg GIT_SHA="$GIT_SHA" \
  -t "${REGION}-docker.pkg.dev/${PROJECT_ID}/zeeq/zeeq-runtime" \
  -f build/Dockerfile .
