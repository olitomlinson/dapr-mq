#!/usr/bin/env bash
# Creates a kind cluster (or reuses one that already exists) and prints its kube context on
# stdout, so it can be piped into k8s-base-deploy.sh and daprmq-deploy.sh:
#
#   ./kind-create.sh | ./k8s-base-deploy.sh | ./daprmq-deploy.sh
#
# All progress output goes to stderr; stdout carries only the context name.
#
# Usage:
#   ./kind-create.sh [options]
#
# Options:
#   --name NAME         kind cluster name (default: daprmq-e2e)
#   --image IMAGE       kind node image, e.g. kindest/node:v1.33.1 (default: kind's own default)
#   -h, --help          Show this help and exit
#
# Delete it again with: kind delete cluster --name <name>

set -euo pipefail

NAME="daprmq-e2e"
IMAGE=""

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --name) NAME="$2"; shift 2 ;;
        --image) IMAGE="$2"; shift 2 ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1" >&2; show_help >&2; exit 1 ;;
    esac
done

exec 3>&1 1>&2

if kind get clusters 2>/dev/null | grep -qx "$NAME"; then
    echo "== kind cluster ${NAME} already exists, reusing it"
else
    echo "== Creating kind cluster ${NAME}"
    args=(create cluster --name "$NAME" --wait 2m)
    [[ -n "$IMAGE" ]] && args+=(--image "$IMAGE")
    kind "${args[@]}"
fi

echo "kind-${NAME}" >&3
