#!/usr/bin/env bash
# Installs DaprMQ's dependencies into an existing Kubernetes cluster: the Dapr control plane,
# KEDA, Postgres, and the `statestore` Component DaprMQ uses. DaprMQ itself is not installed -
# that's daprmq-deploy.sh. Prints the kube context on stdout so the next script can be piped on:
#
#   ./kind-create.sh | ./k8s-base-deploy.sh | ./daprmq-deploy.sh
#
# The context comes from --context, or else the first line of stdin when stdin is piped. There
# is no fallback to the current kubectl context, so it can't land on a cluster by accident.
#
# Re-runnable: Dapr and KEDA are `helm upgrade --install`ed. Postgres and the Component are left
# exactly as they are if they already exist (a stateful dependency is never upgraded).
#
# Usage:
#   ./k8s-base-deploy.sh [options]
#
# Options:
#   --context NAME          kube context to deploy into (default: read from stdin)
#   --namespace NAME        Namespace for Postgres and the statestore Component - the namespace
#                            DaprMQ will be installed into (default: daprmq)
#   --dapr-namespace NAME   Namespace for the Dapr control plane (default: dapr-system)
#   --dapr-version VERSION  Pin the Dapr Helm chart version (default: latest)
#   --skip-keda             Don't install KEDA
#   -h, --help              Show this help and exit
#
# Environment: POSTGRES_PASSWORD (default: daprmq_secret_123)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTEXT=""
NAMESPACE="daprmq"
DAPR_NAMESPACE="dapr-system"
DAPR_VERSION=""
SKIP_KEDA="false"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-daprmq_secret_123}"
STATESTORE_NAME="statestore"

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --context) CONTEXT="$2"; shift 2 ;;
        --namespace) NAMESPACE="$2"; shift 2 ;;
        --dapr-namespace) DAPR_NAMESPACE="$2"; shift 2 ;;
        --dapr-version) DAPR_VERSION="$2"; shift 2 ;;
        --skip-keda) SKIP_KEDA="true"; shift ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1" >&2; show_help >&2; exit 1 ;;
    esac
done

if [[ -z "$CONTEXT" && ! -t 0 ]]; then read -r CONTEXT || true; fi
[[ -n "$CONTEXT" ]] || { echo "No kube context: pass --context, or pipe one in (e.g. from kind-create.sh)" >&2; exit 1; }

exec 3>&1 1>&2

k() { kubectl --context "$CONTEXT" "$@"; }
h() { helm --kube-context "$CONTEXT" "$@"; }

k get nodes >/dev/null || { echo "Cannot reach the cluster for context ${CONTEXT}"; exit 1; }

echo "== Helm repos"
repos=(dapr=https://dapr.github.io/helm-charts/ bitnami=https://charts.bitnami.com/bitnami)
[[ "$SKIP_KEDA" == "true" ]] || repos+=(kedacore=https://kedacore.github.io/charts)
for repo in "${repos[@]}"; do
    helm repo list 2>/dev/null | awk '{print $1}' | grep -qx "${repo%%=*}" || helm repo add "${repo%%=*}" "${repo#*=}"
done
helm repo update "${repos[@]%%=*}" >/dev/null

echo "== Dapr control plane (${DAPR_NAMESPACE})"
dapr_args=(upgrade --install dapr dapr/dapr -n "$DAPR_NAMESPACE" --create-namespace --wait --timeout 5m0s)
[[ -n "$DAPR_VERSION" ]] && dapr_args+=(--version "$DAPR_VERSION")
h "${dapr_args[@]}" >/dev/null

if [[ "$SKIP_KEDA" != "true" ]]; then
    echo "== KEDA (keda)"
    h upgrade --install keda kedacore/keda -n keda --create-namespace --wait --timeout 5m0s >/dev/null
fi

k create namespace "$NAMESPACE" --dry-run=client -o yaml | k apply -f - >/dev/null

# Resources (and any other shared settings) for the chart: see postgres-values.yaml.
POSTGRES_VALUES="$SCRIPT_DIR/postgres-values.yaml"

echo "== Postgres (${NAMESPACE})"
if h status postgres -n "$NAMESPACE" >/dev/null 2>&1; then
    echo "   already installed, leaving it as-is"
    cpu_limit="$(k get statefulset postgres-postgresql -n "$NAMESPACE" \
        -o jsonpath='{.spec.template.spec.containers[0].resources.limits.cpu}' 2>/dev/null || true)"
    if [[ "$cpu_limit" =~ ^([0-9]+)m$ ]] && (( BASH_REMATCH[1] < 1000 )); then
        echo "   its CPU limit is $cpu_limit, which throttles DaprMQ. To raise it (Postgres restarts, data is kept):" >&2
        echo "     helm upgrade postgres bitnami/postgresql -n $NAMESPACE --reuse-values --version <installed chart version> -f $POSTGRES_VALUES" >&2
    fi
else
    h install postgres bitnami/postgresql -n "$NAMESPACE" \
        --set auth.postgresPassword="$POSTGRES_PASSWORD" --set auth.database=actor_state \
        -f "$POSTGRES_VALUES" \
        --wait --timeout 5m0s >/dev/null
fi

echo "== State store Component ${STATESTORE_NAME} (${NAMESPACE})"
if k get component "$STATESTORE_NAME" -n "$NAMESPACE" >/dev/null 2>&1; then
    echo "   already exists, leaving it as-is"
else
    cat <<EOF | k apply -f - >/dev/null
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: $STATESTORE_NAME
  namespace: $NAMESPACE
spec:
  type: state.postgresql
  version: v2
  metadata:
    - name: connectionString
      value: "host=postgres-postgresql.${NAMESPACE}.svc.cluster.local user=postgres password=${POSTGRES_PASSWORD} port=5432 database=actor_state connect_timeout=10"
    - name: actorStateStore
      value: "true"
    - name: tablePrefix
      value: daprmq_
EOF
fi

echo "$CONTEXT" >&3
