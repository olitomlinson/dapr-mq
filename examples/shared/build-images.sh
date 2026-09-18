#!/usr/bin/env bash
# Builds all 8 example images (producer + consumer x dotnet/java/python/typescript).
# Must be run with the repo root as build context, since none of the SDKs are
# published - each Dockerfile COPYs its SDK source directly from sdks/<lang>/.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG="${TAG:-0.1.0}"

cd "$REPO_ROOT"

for lang in dotnet java python typescript; do
  for role in producer consumer; do
    image="daprmq-examples-${lang}-${role}:${TAG}"
    echo "==> Building ${image} (examples/${lang}/${role}/Dockerfile, context: ${REPO_ROOT})"
    docker build \
      -f "examples/${lang}/${role}/Dockerfile" \
      -t "${image}" \
      .
  done
done

echo "Done. Built images:"
docker images --filter "reference=daprmq-examples-*" --format '  {{.Repository}}:{{.Tag}}'
