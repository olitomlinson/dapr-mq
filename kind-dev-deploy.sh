#!/usr/bin/env bash
# Builds the DaprMQ server and operator images from current source, loads them into a kind cluster,
# and upgrades an existing DaprMQ release to run them - no release tag or registry push needed.
# Only the daprmq release is touched: Dapr, KEDA, Postgres and the examples are left as they are,
# and the release's own values (console, operator, state store...) are kept.
#
# The images are tagged :dev every time, so a rebuild doesn't change the pod template; the
# gateway, worker and operator are restarted so they pick the new image up.
#
# Usage:
#   ./kind-dev-deploy.sh [options]
#
# Options:
#   --cluster NAME      kind cluster to load the images into (default: daprmq-e2e)
#   --namespace NAME    Namespace of the DaprMQ release (default: daprmq)
#   --release NAME      DaprMQ release name (default: daprmq)
#   --skip-build        Load and deploy the :dev images already built locally
#   -h, --help          Show this help and exit
#
# Back to a published release:
#   helm upgrade <release> oci://ghcr.io/olitomlinson/charts/daprmq --version <v> -n <namespace> \
#     --reset-then-reuse-values --set image.registry=ghcr.io/olitomlinson --set image.tag= \
#     --set image.pullPolicy=IfNotPresent --set operator.image.tag=

set -euo pipefail

CLUSTER="daprmq-e2e"
NAMESPACE="daprmq"
RELEASE="daprmq"
SKIP_BUILD="false"
TAG="dev"

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --cluster) CLUSTER="$2"; shift 2 ;;
        --namespace) NAMESPACE="$2"; shift 2 ;;
        --release) RELEASE="$2"; shift 2 ;;
        --skip-build) SKIP_BUILD="true"; shift ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1"; show_help; exit 1 ;;
    esac
done

CONTEXT="kind-${CLUSTER}"
cd "$(dirname "${BASH_SOURCE[0]}")"

# Same naming rule as the chart's daprmq.fullname (no fullnameOverride).
if [[ "$RELEASE" == *daprmq* ]]; then FULLNAME="$RELEASE"; else FULLNAME="${RELEASE}-daprmq"; fi

helm --kube-context "$CONTEXT" status "$RELEASE" -n "$NAMESPACE" >/dev/null \
    || { echo "No release ${RELEASE} in ${NAMESPACE} on ${CONTEXT}: install DaprMQ first (k8s-deploy-and-test.sh)"; exit 1; }

if [[ "$SKIP_BUILD" != "true" ]]; then
    echo "== Building daprmq:${TAG} and daprmq-operator:${TAG}"
    docker build -q -t "daprmq:${TAG}" ./server
    docker build -q -t "daprmq-operator:${TAG}" -f ./server/Dockerfile.operator .
fi

echo "== Loading into kind cluster ${CLUSTER}"
kind load docker-image --name "$CLUSTER" "daprmq:${TAG}" "daprmq-operator:${TAG}"

echo "== Upgrading ${NAMESPACE}/${RELEASE}"
helm --kube-context "$CONTEXT" upgrade "$RELEASE" ./helm -n "$NAMESPACE" --reset-then-reuse-values \
    --set image.registry= --set image.tag="$TAG" --set image.pullPolicy=Never --set operator.image.tag="$TAG" \
    --wait --timeout 5m >/dev/null

deployments=()
for d in gateway worker operator; do
    kubectl --context "$CONTEXT" -n "$NAMESPACE" get deploy "${FULLNAME}-${d}" >/dev/null 2>&1 && deployments+=("${FULLNAME}-${d}")
done
for d in "${deployments[@]}"; do kubectl --context "$CONTEXT" -n "$NAMESPACE" rollout restart "deploy/$d" >/dev/null; done
for d in "${deployments[@]}"; do kubectl --context "$CONTEXT" -n "$NAMESPACE" rollout status "deploy/$d" --timeout=180s; done

kubectl --context "$CONTEXT" -n "$NAMESPACE" get deploy "${deployments[@]}" \
    -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.template.spec.containers[0].image}{"\n"}{end}'
