set -e

# gcloud auth configure-docker us-central1-docker.pkg.dev

# Pack first
./build/pack.sh

# Deploy to Cloud Run
./build/ship.sh
./build/ship-worker.sh

echo "Shipped $(git rev-parse --short=8 HEAD) 🚀"
