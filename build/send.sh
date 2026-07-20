set -e

# gcloud auth configure-docker us-central1-docker.pkg.dev

# Pack first
# By default this embeds dev-<shortsha> as the display version. To stamp an
# explicit release version into /health and the user menu, pass the SemVer value
# through pack.sh instead, for example: ./build/pack.sh 1.0.0-rc.1
./build/pack.sh

# Deploy to Cloud Run
./build/ship.sh
./build/ship-worker.sh

echo "Shipped $(git rev-parse --short=8 HEAD) 🚀"
