#!/usr/bin/env bash
# Deploys DaprMQ and the example apps into a cluster that already has its dependencies
# (k8s-base-deploy.sh). DaprMQ is either built from current source (the default) or a published
# chart version (--version). The examples are always built from source. Prints the kube context
# on stdout, like the other scripts in the chain:
#
#   ./kind-create.sh | ./k8s-base-deploy.sh | ./daprmq-deploy.sh
#
# The context comes from --context, or else the first line of stdin when stdin is piped.
#
# Built images go to the cluster with `kind load` when the context is kind-<cluster>; any other
# context is assumed to share the local Docker daemon (Docker Desktop). Local images are tagged
# :dev (examples :0.1.0) every time, so a rebuild doesn't change the pod template - the
# deployments are restarted to pick the new image up.
#
# If KEDA is installed, the operator (KEDA external scaler) and the examples' autoscale workers
# are switched on. Other values already set on the releases (console, ...) are kept.
#
# Usage:
#   ./daprmq-deploy.sh [options]
#
# Options:
#   --context NAME              kube context to deploy into (default: read from stdin)
#   --version VERSION           Deploy this published chart version from
#                                oci://ghcr.io/olitomlinson/charts/daprmq instead of local source
#   --namespace NAME            Namespace of the DaprMQ release (default: daprmq)
#   --release NAME              DaprMQ release name (default: daprmq)
#   --examples-namespace NAME   Namespace of the example apps (default: examples)
#   --skip-examples             Don't build or deploy the example apps
#   --skip-build                Deploy the images already built locally
#   -h, --help                  Show this help and exit

set -euo pipefail

CONTEXT=""
VERSION=""
NAMESPACE="daprmq"
RELEASE="daprmq"
EXAMPLES_NAMESPACE="examples"
SKIP_EXAMPLES="false"
SKIP_BUILD="false"
TAG="dev"
EXAMPLES_TAG="0.1.0"
CHART_OCI="oci://ghcr.io/olitomlinson/charts/daprmq"

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --context) CONTEXT="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --namespace) NAMESPACE="$2"; shift 2 ;;
        --release) RELEASE="$2"; shift 2 ;;
        --examples-namespace) EXAMPLES_NAMESPACE="$2"; shift 2 ;;
        --skip-examples) SKIP_EXAMPLES="true"; shift ;;
        --skip-build) SKIP_BUILD="true"; shift ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1" >&2; show_help >&2; exit 1 ;;
    esac
done

if [[ -z "$CONTEXT" && ! -t 0 ]]; then read -r CONTEXT || true; fi
[[ -n "$CONTEXT" ]] || { echo "No kube context: pass --context, or pipe one in (e.g. from k8s-base-deploy.sh)" >&2; exit 1; }

exec 3>&1 1>&2
cd "$(dirname "${BASH_SOURCE[0]}")"

k() { kubectl --context "$CONTEXT" "$@"; }
h() { helm --kube-context "$CONTEXT" "$@"; }

k get component statestore -n "$NAMESPACE" >/dev/null \
    || { echo "No statestore Component in ${NAMESPACE} on ${CONTEXT}: run k8s-base-deploy.sh first"; exit 1; }
KEDA="false"
k get crd scaledobjects.keda.sh >/dev/null 2>&1 && KEDA="true"

# Same naming rule as the chart's daprmq.fullname (no fullnameOverride).
if [[ "$RELEASE" == *daprmq* ]]; then FULLNAME="$RELEASE"; else FULLNAME="${RELEASE}-daprmq"; fi

images=()
if [[ -z "$VERSION" ]]; then images+=("daprmq:${TAG}" "daprmq-operator:${TAG}"); fi
if [[ "$SKIP_EXAMPLES" != "true" ]]; then
    for lang in dotnet java python typescript; do
        images+=("daprmq-examples-${lang}-producer:${EXAMPLES_TAG}" "daprmq-examples-${lang}-consumer:${EXAMPLES_TAG}")
    done
fi

if [[ "$SKIP_BUILD" != "true" ]]; then
    if [[ -z "$VERSION" ]]; then
        echo "== Building daprmq:${TAG} and daprmq-operator:${TAG}"
        docker build -q -t "daprmq:${TAG}" ./server
        docker build -q -t "daprmq-operator:${TAG}" -f ./server/Dockerfile.operator .
    fi
    if [[ "$SKIP_EXAMPLES" != "true" ]]; then
        echo "== Building the example images"
        TAG="$EXAMPLES_TAG" ./examples/shared/build-images.sh
    fi
fi

if [[ ${#images[@]} -gt 0 && "$CONTEXT" == kind-* ]]; then
    echo "== Loading ${#images[@]} image(s) into kind cluster ${CONTEXT#kind-}"
    kind load docker-image --name "${CONTEXT#kind-}" "${images[@]}"
fi

restart_all() {
    local ns="$1" d
    local deployments=($(k get deploy -n "$ns" -o jsonpath='{.items[*].metadata.name}'))
    [[ ${#deployments[@]} -gt 0 ]] || return 0
    k rollout restart deploy -n "$ns" >/dev/null
    for d in "${deployments[@]}"; do k rollout status "deploy/$d" -n "$ns" --timeout=180s; done
}

echo "== DaprMQ ${VERSION:-from source} (${NAMESPACE}/${RELEASE})"
daprmq_args=(upgrade --install "$RELEASE" -n "$NAMESPACE" --reset-then-reuse-values
    --set dapr.stateStoreName=statestore --set operator.enabled="$KEDA" --wait --timeout 5m0s)
if [[ -z "$VERSION" ]]; then
    daprmq_args+=(./helm --set image.registry= --set image.tag="$TAG" --set image.pullPolicy=Never
        --set operator.image.tag="$TAG")
else
    daprmq_args+=("$CHART_OCI" --version "$VERSION" --set image.registry=ghcr.io/olitomlinson --set image.tag=
        --set image.pullPolicy=IfNotPresent --set operator.image.tag=)
fi
h "${daprmq_args[@]}" >/dev/null
[[ -z "$VERSION" ]] && restart_all "$NAMESPACE"

if [[ "$SKIP_EXAMPLES" != "true" ]]; then
    echo "== Example apps (${EXAMPLES_NAMESPACE})"
    h upgrade --install daprmq-examples ./examples/helm -n "$EXAMPLES_NAMESPACE" --create-namespace \
        --reset-then-reuse-values --set daprmq.namespace="$NAMESPACE" --set daprmq.releaseName="$RELEASE" \
        --set autoscale.enabled="$KEDA" --wait --timeout 5m0s >/dev/null
    restart_all "$EXAMPLES_NAMESPACE"
fi

k get deploy -n "$NAMESPACE" -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.template.spec.containers[0].image}{"\n"}{end}' \
    | grep "^${FULLNAME}-" || true

echo "$CONTEXT" >&3
