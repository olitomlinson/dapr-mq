#!/usr/bin/env bash
# Bootstraps a bare-bones Kubernetes cluster (as shipped by Docker Desktop) with every
# DaprMQ dependency, deploys DaprMQ + all 8 example apps built from current source, then
# drives a full end-to-end run through every example scenario for every language SDK -
# checking both the HTTP response contract and each container's own stdout logs.
#
# Re-runnable: every DaprMQ-owned release is `helm upgrade --install`ed, so a second run
# upgrades in place instead of erroring or leaving stale state. The bitnami Postgres
# state-store dependency is the one exception - if it's already installed, this script
# leaves it exactly as-is (never uninstalls/upgrades a stateful database dependency).
#
# Usage:
#   ./k8s-deploy-and-test.sh [options]
#
# Options:
#   --namespace NAME            Namespace for the daprmq release (default: daprmq)
#   --examples-namespace NAME   Namespace for the example apps (default: examples)
#   --dapr-namespace NAME       Namespace for the Dapr control plane (default: dapr-system)
#   --context NAME              Expected kubectl context (default: docker-desktop)
#   --force                     Skip the kubectl-context safety check
#   --image-tag TAG             Tag to build/deploy the daprmq server image as (default: dev)
#   --languages CSV             Comma-separated example languages to test
#                                (default: dotnet,java,python,typescript)
#   --scenario NAME             Run only this one scenario instead of all four
#                                (basic|ack-deadletter|priority|sessions|idempotency)
#   --skip-e2e                  Deploy everything but skip the scenario run-through
#   --skip-build                Skip `docker build` steps and deploy whatever images
#                                are already present locally (fast redeploy-only loop)
#   --dapr-version VERSION      Pin the Dapr control-plane Helm chart to this exact
#                                version instead of whatever is currently latest in the
#                                `dapr` repo. NOT the default - omit this flag (the normal
#                                case) and the script always tracks latest, same as before.
#                                Only reach for this to reproduce/test against a specific
#                                Dapr release; it can both upgrade and downgrade the
#                                existing control-plane release.
#   -h, --help                  Show this help and exit
#
# Environment overrides (same effect as the matching flag, flags win if both are set):
#   NAMESPACE, EXAMPLES_NAMESPACE, DAPR_NAMESPACE, KUBE_CONTEXT, IMAGE_TAG,
#   LANGUAGES, POSTGRES_PASSWORD, DAPR_VERSION

set -euo pipefail

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

NAMESPACE="${NAMESPACE:-daprmq}"
EXAMPLES_NAMESPACE="${EXAMPLES_NAMESPACE:-examples}"
DAPR_NAMESPACE="${DAPR_NAMESPACE:-dapr-system}"
KUBE_CONTEXT="${KUBE_CONTEXT:-docker-desktop}"
IMAGE_TAG="${IMAGE_TAG:-dev}"
LANGUAGES="${LANGUAGES:-dotnet,java,python,typescript}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-daprmq_secret_123}"
DAPR_VERSION="${DAPR_VERSION:-}"

DAPR_RELEASE="dapr"
POSTGRES_RELEASE="postgres"
DAPRMQ_RELEASE="daprmq"
EXAMPLES_RELEASE="daprmq-examples"
STATESTORE_NAME="statestore"

ALL_SCENARIOS=(basic ack-deadletter priority sessions idempotency)
SCENARIOS=("${ALL_SCENARIOS[@]}")

FORCE_CONTEXT="false"
SKIP_E2E="false"
SKIP_BUILD="false"

# ---------------------------------------------------------------------------
# Logging helpers (matches build-and-test.sh's style)
# ---------------------------------------------------------------------------

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info()    { echo -e "${BLUE}[INFO]${NC} $*"; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $*"; }
log_warning() { echo -e "${YELLOW}[WARNING]${NC} $*"; }
log_error()   { echo -e "${RED}[ERROR]${NC} $*"; }
log_section() {
    echo ""
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE}$*${NC}"
    echo -e "${BLUE}========================================${NC}"
    echo ""
}

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------

show_help() {
    sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --namespace) NAMESPACE="$2"; shift 2 ;;
        --examples-namespace) EXAMPLES_NAMESPACE="$2"; shift 2 ;;
        --dapr-namespace) DAPR_NAMESPACE="$2"; shift 2 ;;
        --context) KUBE_CONTEXT="$2"; shift 2 ;;
        --force) FORCE_CONTEXT="true"; shift ;;
        --image-tag) IMAGE_TAG="$2"; shift 2 ;;
        --languages) LANGUAGES="$2"; shift 2 ;;
        --scenario)
            SCENARIOS=("$2")
            shift 2
            ;;
        --skip-e2e) SKIP_E2E="true"; shift ;;
        --skip-build) SKIP_BUILD="true"; shift ;;
        --dapr-version) DAPR_VERSION="$2"; shift 2 ;;
        -h|--help) show_help; exit 0 ;;
        *) log_error "Unknown option: $1"; show_help; exit 1 ;;
    esac
done

IFS=',' read -ra LANGUAGE_LIST <<< "$LANGUAGES"
if [[ ${#LANGUAGE_LIST[@]} -eq 0 ]]; then
    log_error "No languages to test (--languages resolved to an empty list)"
    exit 1
fi

# ---------------------------------------------------------------------------
# Result tracking
# ---------------------------------------------------------------------------

declare -a RESULTS=()   # "<label>|PASS" or "<label>|FAIL|<reason>"
FAILED="false"

record_pass() {
    RESULTS+=("$1|PASS")
    log_success "$1"
}

record_fail() {
    RESULTS+=("$1|FAIL|$2")
    FAILED="true"
    log_error "$1 -- $2"
}

# ---------------------------------------------------------------------------
# Port-forward helper - opens a forward, waits until it accepts connections,
# runs the callback, always tears the forward down afterward (even on error).
# ---------------------------------------------------------------------------

PF_PID=""

start_port_forward() {
    local ns="$1" svc="$2" local_port="$3" remote_port="$4"
    kubectl port-forward -n "$ns" "svc/$svc" "${local_port}:${remote_port}" >/dev/null 2>&1 &
    PF_PID=$!
    local tries=0
    until curl -s -o /dev/null -m 1 "http://localhost:${local_port}/health" 2>/dev/null; do
        tries=$((tries + 1))
        if [[ $tries -ge 30 ]]; then
            log_error "Port-forward to ${ns}/${svc} never became reachable"
            stop_port_forward
            return 1
        fi
        if ! kill -0 "$PF_PID" 2>/dev/null; then
            log_error "kubectl port-forward to ${ns}/${svc} exited unexpectedly"
            return 1
        fi
        sleep 0.5
    done
}

stop_port_forward() {
    if [[ -n "$PF_PID" ]] && kill -0 "$PF_PID" 2>/dev/null; then
        kill "$PF_PID" 2>/dev/null || true
        wait "$PF_PID" 2>/dev/null || true
    fi
    PF_PID=""
}

cleanup() {
    stop_port_forward
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# Scenario runner with retry - a fresh deploy's actor placement can take a few
# seconds to fully stabilize, which surfaces as an occasional transient 502
# UPSTREAM_ERROR right after rollout. Retry a few times with backoff before
# treating it as a real failure. Sets RS_STATUS / RS_BODY; returns 0 only on
# a genuine HTTP 200.
# ---------------------------------------------------------------------------

RS_STATUS=""
RS_BODY=""

run_scenario_with_retry() {
    local port="$1" scenario="$2" curl_timeout="$3" max_attempts="${4:-5}"
    local attempt=1 response

    while true; do
        response="$(curl -s -w '\n%{http_code}' -m "$curl_timeout" -X POST "http://localhost:${port}/scenarios/${scenario}/run" || echo -e "\n000")"
        RS_STATUS="$(echo "$response" | tail -n1)"
        RS_BODY="$(echo "$response" | sed '$d')"

        [[ "$RS_STATUS" == "200" ]] && return 0

        if [[ "$RS_STATUS" == "502" && $attempt -lt $max_attempts ]]; then
            log_warning "  scenario '${scenario}' got HTTP 502 (attempt ${attempt}/${max_attempts}) - retrying in $((attempt * 3))s, likely transient actor-placement settling"
            sleep $((attempt * 3))
            attempt=$((attempt + 1))
            continue
        fi
        return 1
    done
}

# ---------------------------------------------------------------------------
# Force every Deployment in a namespace to roll fresh pods. This matters
# because `helm upgrade --install` alone is NOT enough to pick up a rebuilt
# local image when the tag string doesn't change (image.tag=dev / :0.1.0
# stay constant across dev-loop iterations by design, per
# helm/CONTRIBUTOR_GUIDE.md's Fast Development Loop) - Kubernetes only
# re-pulls/recreates a container on a Pod template diff, and an unchanged
# image string is not one, even with pullPolicy Never/IfNotPresent. Without
# this, a code change can sit fully built on disk while already-running pods
# keep serving the previous image's binary indefinitely.
# ---------------------------------------------------------------------------

rollout_restart_all() {
    local ns="$1"
    local deployments
    deployments="$(kubectl get deployments -n "$ns" -o jsonpath='{.items[*].metadata.name}' 2>/dev/null || true)"

    if [[ -z "$deployments" ]]; then
        return 0
    fi

    kubectl rollout restart deployment -n "$ns" >/dev/null

    local name
    for name in $deployments; do
        kubectl rollout status "deployment/${name}" -n "$ns" --timeout=180s >/dev/null
    done
}

# ---------------------------------------------------------------------------
# Step 0: Prerequisite checks
# ---------------------------------------------------------------------------

log_section "Checking Prerequisites"

for cmd in kubectl helm docker curl; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        log_error "Required command not found: $cmd"
        exit 1
    fi
done
log_success "kubectl, helm, docker, curl are all installed"

if ! docker info >/dev/null 2>&1; then
    log_error "Docker daemon is not running (docker info failed)"
    exit 1
fi
log_success "Docker daemon is reachable"

CURRENT_CONTEXT="$(kubectl config current-context 2>/dev/null || echo "")"
if [[ -z "$CURRENT_CONTEXT" ]]; then
    log_error "No current kubectl context set"
    exit 1
fi

if [[ "$CURRENT_CONTEXT" != "$KUBE_CONTEXT" ]]; then
    if [[ "$FORCE_CONTEXT" != "true" ]]; then
        log_error "Current kubectl context is '$CURRENT_CONTEXT', expected '$KUBE_CONTEXT'."
        log_error "This script deploys and upgrades live cluster state - refusing to run against an unexpected context."
        log_error "Pass --context '$CURRENT_CONTEXT' (or --force) if this is intentional."
        exit 1
    fi
    log_warning "Current context '$CURRENT_CONTEXT' != expected '$KUBE_CONTEXT', continuing anyway (--force)"
else
    log_success "kubectl context: $CURRENT_CONTEXT"
fi

if ! kubectl get nodes >/dev/null 2>&1; then
    log_error "Cannot reach the Kubernetes API server (kubectl get nodes failed)"
    exit 1
fi
log_success "Cluster is reachable"

# ---------------------------------------------------------------------------
# Step 1: Build every image up front, before touching the cluster at all.
#
# Deliberately done before any deploy/upgrade step: building 8+ images
# (Java/Maven and dotnet in particular) is heavy, sustained CPU load, and
# Docker Desktop's Kubernetes shares that same CPU with the cluster. Doing
# this while a just-restarted daprmq pod is trying to (re-)register with
# Dapr's actor placement service has been observed to starve that
# registration and produce transient "UPSTREAM_ERROR" 502s a few minutes
# later - even though a `helm test` run immediately after the restart
# passes. Building everything first removes that overlap entirely.
# ---------------------------------------------------------------------------

log_section "Building Images"

if [[ "$SKIP_BUILD" == "true" ]]; then
    log_warning "Skipping docker build (--skip-build) - deploying whatever images are already present locally"
else
    docker build -t "daprmq:${IMAGE_TAG}" ./server
    log_success "Built daprmq:${IMAGE_TAG}"

    ./examples/shared/build-images.sh
    log_success "Built all 8 example images"
fi

# ---------------------------------------------------------------------------
# Step 2: Namespaces
# ---------------------------------------------------------------------------

log_section "Ensuring Namespaces"

for ns in "$NAMESPACE" "$EXAMPLES_NAMESPACE"; do
    kubectl create namespace "$ns" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
    log_success "Namespace ready: $ns"
done

# ---------------------------------------------------------------------------
# Step 3: Helm repos
# ---------------------------------------------------------------------------

log_section "Ensuring Helm Repos"

if ! helm repo list 2>/dev/null | awk '{print $1}' | grep -qx "dapr"; then
    helm repo add dapr https://dapr.github.io/helm-charts/
fi
if ! helm repo list 2>/dev/null | awk '{print $1}' | grep -qx "bitnami"; then
    helm repo add bitnami https://charts.bitnami.com/bitnami
fi
helm repo update dapr bitnami >/dev/null
log_success "Helm repos (dapr, bitnami) ready"

# ---------------------------------------------------------------------------
# Step 4: Dapr control plane
# ---------------------------------------------------------------------------

log_section "Deploying Dapr Control Plane"

dapr_helm_args=(upgrade --install "$DAPR_RELEASE" dapr/dapr -n "$DAPR_NAMESPACE" --create-namespace --wait --timeout 5m0s)
if [[ -n "$DAPR_VERSION" ]]; then
    log_warning "Forcing Dapr control-plane chart version ${DAPR_VERSION} (--dapr-version) - default is latest, so only do this deliberately (e.g. to reproduce/test against a specific Dapr release)."
    dapr_helm_args+=(--version "$DAPR_VERSION")
else
    log_info "No --dapr-version given - tracking latest available Dapr control-plane chart version, as usual."
fi

helm "${dapr_helm_args[@]}"

log_success "Dapr control plane deployed/upgraded in namespace $DAPR_NAMESPACE"

# ---------------------------------------------------------------------------
# Step 5: Postgres state store (left alone if it already exists)
# ---------------------------------------------------------------------------

log_section "Ensuring Postgres State Store"

if helm status "$POSTGRES_RELEASE" -n "$NAMESPACE" >/dev/null 2>&1; then
    log_info "Release '$POSTGRES_RELEASE' already installed in '$NAMESPACE' - leaving it as-is (not upgrading a stateful database dependency)."
else
    helm install "$POSTGRES_RELEASE" bitnami/postgresql \
        -n "$NAMESPACE" \
        --set auth.postgresPassword="$POSTGRES_PASSWORD" \
        --set auth.database=actor_state \
        --wait --timeout 5m0s
    log_success "Postgres installed in namespace $NAMESPACE"
fi

# ---------------------------------------------------------------------------
# Step 6: State store Component CR (left alone if it already exists, so a
# pre-existing Postgres install with a different password isn't clobbered)
# ---------------------------------------------------------------------------

log_section "Ensuring DaprMQ State Store Component"

if kubectl get component "$STATESTORE_NAME" -n "$NAMESPACE" >/dev/null 2>&1; then
    log_info "Component '$STATESTORE_NAME' already exists in '$NAMESPACE' - leaving it as-is."
else
    cat <<EOF | kubectl apply -f -
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
      value: "host=${POSTGRES_RELEASE}-postgresql.${NAMESPACE}.svc.cluster.local user=postgres password=${POSTGRES_PASSWORD} port=5432 database=actor_state connect_timeout=10"
    - name: actorStateStore
      value: "true"
    - name: tablePrefix
      value: daprmq_
EOF
    log_success "State store Component '$STATESTORE_NAME' created"
fi

# ---------------------------------------------------------------------------
# Step 7: Deploy the DaprMQ server (image already built in Step 1)
# ---------------------------------------------------------------------------

log_section "Deploying DaprMQ"

helm upgrade --install "$DAPRMQ_RELEASE" ./helm \
    -n "$NAMESPACE" \
    --set dapr.stateStoreName="$STATESTORE_NAME" \
    --set image.tag="$IMAGE_TAG" \
    --set image.pullPolicy=Never \
    --wait --timeout 5m0s

log_success "DaprMQ deployed/upgraded in namespace $NAMESPACE"

log_info "Restarting DaprMQ pods to guarantee they're running the image just built (not a stale one from a previous run of this script)..."
rollout_restart_all "$NAMESPACE"
log_success "DaprMQ pods rolled and ready"

log_info "Running the DaprMQ chart's built-in enqueue/dequeue smoke test..."
if helm test "$DAPRMQ_RELEASE" -n "$NAMESPACE" --logs; then
    record_pass "helm test daprmq (enqueue/dequeue smoke test)"
else
    record_fail "helm test daprmq (enqueue/dequeue smoke test)" "helm test exited non-zero"
fi

# ---------------------------------------------------------------------------
# Step 8: Deploy the example apps (images already built in Step 1)
# ---------------------------------------------------------------------------

log_section "Deploying Example Apps"

helm upgrade --install "$EXAMPLES_RELEASE" ./examples/helm \
    -n "$EXAMPLES_NAMESPACE" \
    --set daprmq.namespace="$NAMESPACE" \
    --set daprmq.releaseName="$DAPRMQ_RELEASE" \
    --wait --timeout 5m0s

log_success "Example apps deployed/upgraded in namespace $EXAMPLES_NAMESPACE"

log_info "Restarting example app pods to guarantee they're running the images just built (not stale ones from a previous run of this script)..."
rollout_restart_all "$EXAMPLES_NAMESPACE"
log_success "Example app pods rolled and ready"

log_info "Running the example chart's built-in health-check test..."
if helm test "$EXAMPLES_RELEASE" -n "$EXAMPLES_NAMESPACE" --logs; then
    record_pass "helm test daprmq-examples (8x /health)"
else
    record_fail "helm test daprmq-examples (8x /health)" "helm test exited non-zero"
fi

# ---------------------------------------------------------------------------
# Step 9: End-to-end scenario run-through
# ---------------------------------------------------------------------------

if [[ "$SKIP_E2E" == "true" ]]; then
    log_warning "Skipping E2E scenario run-through (--skip-e2e)"
else
    log_section "Running E2E Scenarios: ${SCENARIOS[*]} x ${LANGUAGE_LIST[*]}"

    for lang in "${LANGUAGE_LIST[@]}"; do
        log_info "--- ${lang} ---"

        # Baseline log line counts, so the presence check below can confirm each
        # pod's stdout actually grew in response to our requests - more robust
        # than grepping for exact wording, which varies scenario-to-scenario and
        # language-to-language (e.g. the `priority` scenario's log line doesn't
        # always restate the queue id the way `basic`'s does).
        producer_log_before="$(kubectl logs -n "$EXAMPLES_NAMESPACE" "deploy/daprmq-examples-${lang}-producer" --tail=-1 2>/dev/null | wc -l | tr -d ' ')"
        consumer_log_before="$(kubectl logs -n "$EXAMPLES_NAMESPACE" "deploy/daprmq-examples-${lang}-consumer" --tail=-1 2>/dev/null | wc -l | tr -d ' ')"

        # Producer: run every scenario against the producer control API.
        if start_port_forward "$EXAMPLES_NAMESPACE" "daprmq-examples-${lang}-producer" 18080 8080; then
            health="$(curl -s -o /dev/null -w '%{http_code}' -m 5 http://localhost:18080/health || echo "000")"
            if [[ "$health" != "200" ]]; then
                record_fail "${lang} producer /health" "got HTTP $health"
            fi

            for scenario in "${SCENARIOS[@]}"; do
                if run_scenario_with_retry 18080 "$scenario" 30 && echo "$RS_BODY" | grep -q '"steps"'; then
                    record_pass "${lang} producer scenario '${scenario}'"
                else
                    record_fail "${lang} producer scenario '${scenario}'" "HTTP $RS_STATUS: $(echo "$RS_BODY" | head -c 200)"
                fi
            done
            stop_port_forward
        else
            record_fail "${lang} producer port-forward" "could not reach service"
        fi

        # Consumer: run the same scenarios against the matching consumer.
        if start_port_forward "$EXAMPLES_NAMESPACE" "daprmq-examples-${lang}-consumer" 18081 8080; then
            health="$(curl -s -o /dev/null -w '%{http_code}' -m 5 http://localhost:18081/health || echo "000")"
            if [[ "$health" != "200" ]]; then
                record_fail "${lang} consumer /health" "got HTTP $health"
            fi

            for scenario in "${SCENARIOS[@]}"; do
                # ack-deadletter deliberately sleeps ~11s for a lock to expire.
                if run_scenario_with_retry 18081 "$scenario" 60 && echo "$RS_BODY" | grep -q '"steps"'; then
                    record_pass "${lang} consumer scenario '${scenario}'"
                else
                    record_fail "${lang} consumer scenario '${scenario}'" "HTTP $RS_STATUS: $(echo "$RS_BODY" | head -c 200)"
                fi
            done
            stop_port_forward
        else
            record_fail "${lang} consumer port-forward" "could not reach service"
        fi

        # Log presence check: confirm each pod's stdout actually grew by a
        # reasonable amount in response to the runs we just triggered - not
        # just a 200 with no server-side log activity. A line-count delta is
        # more robust than grepping for specific wording, since exactly what
        # gets logged varies by scenario and by language (e.g. `priority`'s
        # log line doesn't always restate the queue id the way `basic`'s does).
        for role in producer consumer; do
            before_var="${role}_log_before"
            log_before="${!before_var}"
            log_after="$(kubectl logs -n "$EXAMPLES_NAMESPACE" "deploy/daprmq-examples-${lang}-${role}" --tail=-1 2>/dev/null | wc -l | tr -d ' ')"
            log_after="${log_after:-0}"
            log_before="${log_before:-0}"
            delta=$((log_after - log_before))

            if [[ $delta -ge ${#SCENARIOS[@]} ]]; then
                record_pass "${lang} ${role} logs narrate the scenario run(s) (+${delta} lines)"
            else
                record_fail "${lang} ${role} logs narrate the scenario run(s)" "only +${delta} new log lines for ${#SCENARIOS[@]} scenario run(s) (before=${log_before}, after=${log_after})"
            fi
        done

        unset queue_ids
    done
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

log_section "Summary"

pass_count=0
fail_count=0
for entry in "${RESULTS[@]:-}"; do
    [[ -z "$entry" ]] && continue
    IFS='|' read -r label status reason <<< "$entry"
    if [[ "$status" == "PASS" ]]; then
        echo -e "  ${GREEN}PASS${NC}  $label"
        pass_count=$((pass_count + 1))
    else
        echo -e "  ${RED}FAIL${NC}  $label ${RED}($reason)${NC}"
        fail_count=$((fail_count + 1))
    fi
done

echo ""
log_info "Total: $((pass_count + fail_count))  Passed: $pass_count  Failed: $fail_count"

if [[ "$FAILED" == "true" ]]; then
    log_error "One or more checks failed."
    exit 1
fi

log_success "All checks passed - DaprMQ and all 4 example SDKs are deployed and verified end-to-end."
