#!/usr/bin/env bash
# Builds all 8 example images and pushes them to a local Docker registry, so a
# kind/minikube (or any cluster that can reach the registry) can pull them without
# a `docker load`/`kind load docker-image` step.
#
# Assumes a registry is already running and reachable, e.g.:
#   docker run -d --restart=always -p 5000:5000 --name registry registry:2
#
# Usage:
#   ./examples/shared/push-images.sh                       # pushes to localhost:5000
#   REGISTRY=my-registry:5000 ./examples/shared/push-images.sh
#   TAG=0.2.0 ./examples/shared/push-images.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REGISTRY="${REGISTRY:-localhost:5000}"
TAG="${TAG:-0.1.0}"

cd "$REPO_ROOT"

for lang in dotnet java python typescript; do
  for role in producer consumer; do
    image="${REGISTRY}/daprmq-examples-${lang}-${role}:${TAG}"
    echo "==> Building ${image} (examples/${lang}/${role}/Dockerfile, context: ${REPO_ROOT})"
    docker build \
      -f "examples/${lang}/${role}/Dockerfile" \
      -t "${image}" \
      .
    echo "==> Pushing ${image}"
    docker push "${image}"
  done
done

echo
echo "Done. Pushed images:"
for lang in dotnet java python typescript; do
  for role in producer consumer; do
    echo "  ${REGISTRY}/daprmq-examples-${lang}-${role}:${TAG}"
  done
done

echo
echo "Install the chart against these images with:"
echo "  helm install daprmq-examples ./examples/helm -n <namespace> \\"
echo "    --set image.registry=${REGISTRY} \\"
echo "    --set image.pullPolicy=Always"
