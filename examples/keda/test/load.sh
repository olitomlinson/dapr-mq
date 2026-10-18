#!/usr/bin/env bash
# KEDA autoscaling load test against the examples chart's autoscale example (examples/shared/SCENARIOS.md#autoscale)
# on whatever cluster it's installed in. Concurrent publishers ramp up one every --ramp-interval seconds and each
# enqueues --messages items to the worker's queue through the DaprMQ gateway; the operator reports the queue depth
# to KEDA, which scales the `<lang>-worker` Deployment out from 0; every replica consumes with competing consumers;
# once the queue is drained KEDA scales it back to 0. Asserted from the Deployment's replica count, the publishers'
# HTTP statuses and the worker pods' own logs:
#
#   1. the worker is idle at 0 replicas before the load
#   2. every publish is accepted (HTTP 200)
#   3. the worker scales out from 0 to >= 2 replicas
#   4. several distinct pods process items
#   5. the queue drains and the worker scales back to 0
#
# Requires KEDA, DaprMQ with operator.enabled=true and the examples chart with autoscale.enabled=true. The
# worker's ScaledObject is found by label, and the gateway from its scaler address, so neither the examples nor
# the DaprMQ namespace and release name need to be given.
#
# Usage:
#   examples/keda/test/load.sh [options]
#
# Options:
#   --context NAME          kubectl context (default: current context)
#   --namespace NAME        Namespace of the examples release (default: wherever its ScaledObject is)
#   --language LANG         Example language whose worker to scale (default: dotnet)
#   --gateway NS/SERVICE    DaprMQ gateway Service (default: derived from the ScaledObject's scalerAddress)
#   --publishers N          Concurrent publishers at full ramp (default: 10)
#   --ramp-interval N       Seconds between starting each publisher (default: 10)
#   --messages N            Messages each publisher enqueues (default: 1000)
#   --publish-mode MODE     single: one message per enqueue call; batch: 100 per call (default: single)
#   --timeout N             Seconds to wait for publishing, the drain and scale-in (default: 1800)
#   -h, --help              Show this help and exit

set -euo pipefail

CONTEXT=""
NAMESPACE=""
LANGUAGE="dotnet"
GATEWAY=""
PUBLISHERS=10
RAMP_INTERVAL=10
MESSAGES=1000
PUBLISH_MODE="single"
TIMEOUT=1800

BATCH_SIZE=100

RED='\033[0;31m'; GREEN='\033[0;32m'; BLUE='\033[0;34m'; NC='\033[0m'
log_info()    { echo -e "${BLUE}[INFO]${NC} $*"; }
log_section() { echo ""; echo -e "${BLUE}== $* ==${NC}"; }

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --context) CONTEXT="$2"; shift 2 ;;
        --namespace) NAMESPACE="$2"; shift 2 ;;
        --language) LANGUAGE="$2"; shift 2 ;;
        --gateway) GATEWAY="$2"; shift 2 ;;
        --publishers) PUBLISHERS="$2"; shift 2 ;;
        --ramp-interval) RAMP_INTERVAL="$2"; shift 2 ;;
        --messages) MESSAGES="$2"; shift 2 ;;
        --publish-mode) PUBLISH_MODE="$2"; shift 2 ;;
        --timeout) TIMEOUT="$2"; shift 2 ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1"; show_help; exit 1 ;;
    esac
done

case "$PUBLISH_MODE" in
    single) STEP=1 ;;
    batch) STEP=$BATCH_SIZE ;;
    *) echo "--publish-mode must be single or batch, got: $PUBLISH_MODE"; exit 1 ;;
esac

kubectl() { command kubectl ${CONTEXT:+--context "$CONTEXT"} "$@"; }

declare -a RESULTS=()
FAILED="false"
pass() { RESULTS+=("PASS|$1"); echo -e "  ${GREEN}PASS${NC} $1"; }
fail() { RESULTS+=("FAIL|$1 ($2)"); FAILED="true"; echo -e "  ${RED}FAIL${NC} $1 ${RED}($2)${NC}"; }

# ---------------------------------------------------------------------------
# Port-forward to the gateway (kubectl picks a free local port).
# ---------------------------------------------------------------------------

PF_PID=""; PF_PORT=""; PF_LOG="$(mktemp)"; PODS_FILE="$(mktemp)"; PUB_DIR="$(mktemp -d)"; LAUNCHER_PID=""
start_port_forward() {
    kubectl port-forward -n "$1" "svc/$2" --address 127.0.0.1 ":8080" >"$PF_LOG" 2>&1 &
    PF_PID=$!
    local tries=0
    until [[ -n "$PF_PORT" ]] && curl -s -o /dev/null -m 1 "http://127.0.0.1:${PF_PORT}/health"; do
        tries=$((tries + 1))
        if [[ $tries -ge 60 ]] || ! kill -0 "$PF_PID" 2>/dev/null; then
            echo "Port-forward to $1/$2 failed: $(head -c 300 "$PF_LOG")"; exit 1
        fi
        PF_PORT="$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) .*/\1/p' "$PF_LOG" | head -n1)"
        sleep 0.5
    done
}

cleanup() {
    if [[ -n "$LAUNCHER_PID" ]]; then pkill -P "$LAUNCHER_PID" 2>/dev/null || true; kill "$LAUNCHER_PID" 2>/dev/null || true; fi
    if [[ -n "$PF_PID" ]]; then kill "$PF_PID" 2>/dev/null || true; wait "$PF_PID" 2>/dev/null || true; fi
    rm -rf "$PF_LOG" "$PODS_FILE" "$PUB_DIR"
}
trap cleanup EXIT

# publisher <id>: enqueues MESSAGES items, STEP per call, then writes "<accepted> <rejected> <first error>"
# to $PUB_DIR/<id>. Items are {"publisher": id, "n": 1..MESSAGES}.
publisher() {
    local id="$1" ok=0 bad=0 err="" from to n items status
    for ((from = 1; from <= MESSAGES; from += STEP)); do
        to=$((from + STEP - 1 < MESSAGES ? from + STEP - 1 : MESSAGES)); items=""
        for ((n = from; n <= to; n++)); do items+="${items:+,}{\"item\":{\"publisher\":${id},\"n\":${n}}}"; done
        status="$(curl -s -o /dev/null -w '%{http_code}' -m 30 -X POST "http://127.0.0.1:${PF_PORT}/queue/${QUEUE}/enqueue" \
            -H 'Content-Type: application/json' -d "{\"items\":[${items}]}" || true)"
        if [[ "$status" == "200" ]]; then ok=$((ok + to - from + 1)); else bad=$((bad + to - from + 1)); err="${err:-HTTP ${status}}"; fi
    done
    echo "$ok $bad ${err}" > "$PUB_DIR/$id"
}

# Starts publishers 1..PUBLISHERS, RAMP_INTERVAL seconds apart, and waits for them all.
launch_publishers() {
    local p
    for ((p = 1; p <= PUBLISHERS; p++)); do
        publisher "$p" &
        [[ $p -lt $PUBLISHERS ]] && sleep "$RAMP_INTERVAL"
    done
    wait
}

publishers_started() {
    local n=$PUBLISHERS
    if [[ $RAMP_INTERVAL -gt 0 ]]; then n=$(( (SECONDS - start) / RAMP_INTERVAL + 1 )); fi
    echo $(( n > PUBLISHERS ? PUBLISHERS : n ))
}
publishers_done() { ls "$PUB_DIR" | grep -c . || true; }

replicas() { local r; r="$(kubectl get deploy "$WORKER" -n "$NAMESPACE" -o jsonpath='{.status.replicas}' 2>/dev/null || true)"; echo "${r:-0}"; }

# Pod names that have logged a processed batch (pods vanish on scale-in, so this is sampled while they're alive).
record_processing_pods() {
    kubectl logs -n "$NAMESPACE" -l "app.kubernetes.io/component=${LANGUAGE}-worker" --prefix --tail=-1 --max-log-requests=50 2>/dev/null \
        | grep "processed [0-9]* items" | sed -E 's#^\[pod/([^/]+)/.*#\1#' >> "$PODS_FILE" || true
    sort -u -o "$PODS_FILE" "$PODS_FILE"
}

# ---------------------------------------------------------------------------
# Find the example and the gateway
# ---------------------------------------------------------------------------

log_section "Setup"
kubectl get crd scaledobjects.keda.sh >/dev/null || { echo "KEDA is not installed (no scaledobjects.keda.sh CRD)"; exit 1; }
selector="app.kubernetes.io/component=${LANGUAGE}-worker"
if [[ -z "$NAMESPACE" ]]; then
    NAMESPACE="$(kubectl get scaledobject -A -l "$selector" -o jsonpath='{.items[0].metadata.namespace}' 2>/dev/null || true)"
fi
SCALED_OBJECT="$(kubectl get scaledobject -n "${NAMESPACE:-default}" -l "$selector" -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
[[ -n "$SCALED_OBJECT" ]] || { echo "No ${LANGUAGE} worker ScaledObject found: install the examples chart with --set autoscale.enabled=true"; exit 1; }
so() { kubectl get scaledobject "$SCALED_OBJECT" -n "$NAMESPACE" -o jsonpath="$1"; }
WORKER="$(so '{.spec.scaleTargetRef.name}')"
QUEUE="$(so '{.spec.triggers[0].metadata.queueId}')"
MAX="$(so '{.spec.maxReplicaCount}')"
COOLDOWN="$(so '{.spec.cooldownPeriod}')"

# <release>-operator.<namespace>[.svc...]:8081 -> <namespace>/<release>-gateway
if [[ -z "$GATEWAY" ]]; then
    scaler_host="$(so '{.spec.triggers[0].metadata.scalerAddress}')"; scaler_host="${scaler_host%%:*}"
    operator_svc="${scaler_host%%.*}"; rest="${scaler_host#*.}"
    GATEWAY="${rest%%.*}/${operator_svc%-operator}-gateway"
fi
GATEWAY_NS="${GATEWAY%%/*}"; GATEWAY_SVC="${GATEWAY#*/}"
kubectl get svc -n "$GATEWAY_NS" "$GATEWAY_SVC" >/dev/null || { echo "No gateway Service ${GATEWAY}: pass --gateway NAMESPACE/SERVICE"; exit 1; }

TOTAL=$((PUBLISHERS * MESSAGES))
log_info "namespace=${NAMESPACE}  worker=${WORKER}  queue=${QUEUE}  max=${MAX}  cooldown=${COOLDOWN}s  gateway=${GATEWAY}"
log_info "publishers=${PUBLISHERS}  ramp-interval=${RAMP_INTERVAL}s  messages=${MESSAGES}/publisher  mode=${PUBLISH_MODE}  total=${TOTAL}"

# ---------------------------------------------------------------------------
# Load
# ---------------------------------------------------------------------------

log_section "Load"

# Baseline: idle queue, so KEDA should have the worker at 0 (allow for cooldown and a previous run's leftovers).
deadline=$((SECONDS + 180))
while [[ "$(replicas)" != "0" && $SECONDS -lt $deadline ]]; do sleep 5; done
if [[ "$(replicas)" == "0" ]]; then
    pass "worker idle at 0 replicas before load"
else
    fail "worker idle at 0 replicas before load" "still at $(replicas) replicas after 180s"
fi

start_port_forward "$GATEWAY_NS" "$GATEWAY_SVC"
start=$SECONDS
launch_publishers &
LAUNCHER_PID=$!

peak=0; scaled_out=""; published_at=""; last_report=-15
deadline=$((start + TIMEOUT))
while [[ $SECONDS -lt $deadline ]]; do
    r="$(replicas)"
    (( r > peak )) && peak=$r
    [[ -z "$scaled_out" && $r -ge 2 ]] && scaled_out=$((SECONDS - start))
    record_processing_pods
    if [[ -z "$published_at" ]] && ! kill -0 "$LAUNCHER_PID" 2>/dev/null; then
        published_at=$((SECONDS - start)); LAUNCHER_PID=""
        log_info "t=${published_at}s all ${PUBLISHERS} publishers finished"
    fi
    # Back at 0 only counts once KEDA has scaled out: batch publishing can finish before the first replica starts.
    [[ -n "$published_at" && "$r" == "0" && $peak -gt 0 ]] && break
    if (( SECONDS - start - last_report >= 15 )); then
        last_report=$((SECONDS - start))
        [[ -z "$published_at" ]] && pubs="publishers=$(publishers_started) started/$(publishers_done) done" || pubs="publishing done"
        log_info "t=${last_report}s ${pubs}  replicas=${r}  pods-processed=$(grep -c . "$PODS_FILE" || true)"
    fi
    sleep 3
done
elapsed=$((SECONDS - start))

# ---------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------

log_section "Checks"
accepted=0; rejected=0; first_error=""
for f in "$PUB_DIR"/*; do
    [[ -f "$f" ]] || continue
    read -r ok bad err < "$f"
    accepted=$((accepted + ok)); rejected=$((rejected + bad)); first_error="${first_error:-$err}"
done
if [[ -n "$published_at" && $accepted -eq $TOTAL ]]; then
    rate="$(awk -v n="$TOTAL" -v s="$published_at" 'BEGIN { printf "%.1f", n / (s > 0 ? s : 1) }')"
    pass "all ${TOTAL} messages published in ${published_at}s (${rate} msg/s, ${PUBLISH_MODE} mode)"
elif [[ -z "$published_at" ]]; then
    fail "all ${TOTAL} messages published" "publishers still running after ${TIMEOUT}s"
else
    fail "all ${TOTAL} messages published" "${accepted} accepted, ${rejected} rejected (first: ${first_error})"
fi

if [[ -n "$scaled_out" ]]; then
    pass "worker scaled out from 0 (>=2 replicas after ${scaled_out}s, peak ${peak}/${MAX})"
else
    fail "worker scaled out from 0" "peak ${peak} replicas"
fi

pods="$(grep -c . "$PODS_FILE" || true)"
if [[ ${pods:-0} -ge 2 ]]; then
    pass "workers consumed in parallel (${pods} pods processed items, peak ${peak} replicas)"
else
    fail "workers consumed in parallel" "only ${pods:-0} pod(s) logged processed items"
fi

if [[ -n "$published_at" && $peak -gt 0 && "$(replicas)" == "0" ]]; then
    pass "queue drained and worker scaled back to 0 (${elapsed}s after the first publish, cooldown ${COOLDOWN}s)"
else
    fail "queue drained and worker scaled back to 0" "$([[ $peak -gt 0 ]] && echo "still at $(replicas) replicas" || echo "never scaled out") after ${elapsed}s"
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

log_section "Summary"
for r in "${RESULTS[@]}"; do
    if [[ "$r" == PASS* ]]; then echo -e "  ${GREEN}PASS${NC} ${r#PASS|}"; else echo -e "  ${RED}FAIL${NC} ${r#FAIL|}"; fi
done
[[ "$FAILED" == "true" ]] && exit 1
echo -e "${GREEN}KEDA autoscale load test passed.${NC}"
