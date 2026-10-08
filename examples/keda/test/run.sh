#!/usr/bin/env bash
# Verifies the KEDA ScaledJob guidance in examples/keda/README.md#long-running-work against a live
# cluster. Each Job (worker.py) dequeues one item with a short lock TTL, works for longer than that
# TTL while heartbeating ExtendLock, then acknowledges.
#
#   1. one Job per item         5 items -> exactly 5 Jobs, each item processed once, locks outlive TTL
#   2. in-flight not recounted  5 in flight + 3 enqueued -> exactly 8 Jobs (locked items count in the
#                               metric and KEDA's default strategy subtracts running Jobs)
#   3. killed Job redelivered   force-delete a Job mid-work -> its item comes back once the lock
#                               expires and a later Job completes it
#   4. push unsupported         an external-push ScaledJob only starts Jobs on its pollingInterval
#
# Requires KEDA and a DaprMQ release with operator.enabled=true. Creates (and deletes) its own
# namespace; queue ids are unique per run.
#
# Usage:
#   examples/keda/test/run.sh [options]
#
# Options:
#   --namespace NAME        Namespace of the DaprMQ release (default: daprmq)
#   --release NAME          DaprMQ release name (default: daprmq)
#   --keda-namespace NAME   Namespace KEDA is installed in (default: keda)
#   --test-namespace NAME   Namespace for the test ScaledJobs (default: daprmq-keda-test)
#   --keep                  Leave the test namespace in place afterwards (for debugging)
#   -h, --help              Show this help and exit

set -euo pipefail

NAMESPACE="daprmq"
RELEASE="daprmq"
KEDA_NAMESPACE="keda"
TEST_NAMESPACE="daprmq-keda-test"
KEEP="false"

TTL=10            # lock TTL; WORK_SECONDS > TTL proves the heartbeat keeps the lock
WORK_SECONDS=30
POLL=5            # ScaledJob pollingInterval
WORKER_IMAGE="python:3.12-alpine"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

RED='\033[0;31m'; GREEN='\033[0;32m'; BLUE='\033[0;34m'; NC='\033[0m'
log_info()    { echo -e "${BLUE}[INFO]${NC} $*"; }
log_section() { echo ""; echo -e "${BLUE}== $* ==${NC}"; }

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --namespace) NAMESPACE="$2"; shift 2 ;;
        --release) RELEASE="$2"; shift 2 ;;
        --keda-namespace) KEDA_NAMESPACE="$2"; shift 2 ;;
        --test-namespace) TEST_NAMESPACE="$2"; shift 2 ;;
        --keep) KEEP="true"; shift ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1"; show_help; exit 1 ;;
    esac
done

# Same naming rule as the chart's daprmq.fullname (no fullnameOverride).
if [[ "$RELEASE" == *daprmq* ]]; then FULLNAME="$RELEASE"; else FULLNAME="${RELEASE}-daprmq"; fi
SCALER_ADDRESS="${FULLNAME}-operator.${NAMESPACE}:8081"
GATEWAY_URL="http://${FULLNAME}-gateway.${NAMESPACE}:8080"
RUN_ID="$(date +%s)"

declare -a RESULTS=()
FAILED="false"
pass() { RESULTS+=("PASS|$1"); echo -e "  ${GREEN}PASS${NC} $1"; }
fail() { RESULTS+=("FAIL|$1 ($2)"); FAILED="true"; echo -e "  ${RED}FAIL${NC} $1 ${RED}($2)${NC}"; }
check() { if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1" "expected $3, got $2"; fi; }

# ---------------------------------------------------------------------------
# Port-forward to the gateway (kubectl picks a free local port) for enqueuing.
# ---------------------------------------------------------------------------

PF_PID=""; PF_PORT=""; PF_LOG="$(mktemp)"
start_port_forward() {
    kubectl port-forward -n "$NAMESPACE" "svc/${FULLNAME}-gateway" --address 127.0.0.1 ":8080" >"$PF_LOG" 2>&1 &
    PF_PID=$!
    local tries=0
    until [[ -n "$PF_PORT" ]] && curl -s -o /dev/null -m 1 "http://127.0.0.1:${PF_PORT}/health"; do
        tries=$((tries + 1))
        if [[ $tries -ge 60 ]] || ! kill -0 "$PF_PID" 2>/dev/null; then
            echo "Port-forward to ${NAMESPACE}/${FULLNAME}-gateway failed: $(head -c 300 "$PF_LOG")"; exit 1
        fi
        PF_PORT="$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) .*/\1/p' "$PF_LOG" | head -n1)"
        sleep 0.5
    done
}

cleanup() {
    if [[ -n "$PF_PID" ]]; then kill "$PF_PID" 2>/dev/null || true; wait "$PF_PID" 2>/dev/null || true; fi
    rm -f "$PF_LOG"
    if [[ "$KEEP" != "true" ]]; then
        kubectl delete namespace "$TEST_NAMESPACE" --wait=false >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

# enqueue <queueId> <first n> <last n>
enqueue() {
    local items="" n
    for n in $(seq "$2" "$3"); do items+="${items:+,}{\"item\":{\"n\":${n}}}"; done
    local status
    status="$(curl -s -o /dev/null -w '%{http_code}' -m 10 -X POST "http://127.0.0.1:${PF_PORT}/queue/$1/enqueue" \
        -H 'Content-Type: application/json' -d "{\"items\":[${items}]}")"
    [[ "$status" == "200" ]] || { echo "enqueue to $1 failed: HTTP $status"; exit 1; }
}

# ---------------------------------------------------------------------------
# ScaledJob helpers. Every Job pod carries the label test=<name>.
# ---------------------------------------------------------------------------

# apply_scaledjob <name> <queueId> <trigger type> [pollingInterval]
apply_scaledjob() {
    cat <<EOF | kubectl apply -n "$TEST_NAMESPACE" -f - >/dev/null
apiVersion: keda.sh/v1alpha1
kind: ScaledJob
metadata:
  name: $1
spec:
  jobTargetRef:
    backoffLimit: 0
    activeDeadlineSeconds: 300
    template:
      metadata:
        labels: {test: $1}
      spec:
        restartPolicy: Never
        containers:
        - name: worker
          image: $WORKER_IMAGE
          imagePullPolicy: IfNotPresent
          command: ["python", "/test/worker.py"]
          env:
          - {name: GATEWAY_URL, value: "$GATEWAY_URL"}
          - {name: QUEUE_ID, value: "$2"}
          - {name: TTL, value: "$TTL"}
          - {name: WORK_SECONDS, value: "$WORK_SECONDS"}
          volumeMounts: [{name: script, mountPath: /test}]
        volumes: [{name: script, configMap: {name: scaledjob-test-worker}}]
  pollingInterval: ${4:-$POLL}
  maxReplicaCount: 20
  successfulJobsHistoryLimit: 50
  failedJobsHistoryLimit: 50
  scalingStrategy:
    strategy: default
  triggers:
  - type: $3
    metadata:
      scalerAddress: $SCALER_ADDRESS
      queueId: $2
      targetValue: "1"
EOF
}

delete_scaledjob() {
    kubectl delete scaledjob "$1" -n "$TEST_NAMESPACE" --wait >/dev/null 2>&1 || true
    kubectl delete jobs -n "$TEST_NAMESPACE" -l "test=$1" --wait >/dev/null 2>&1 || true
}

pod_count() { kubectl get pods -n "$TEST_NAMESPACE" -l "test=$1" --no-headers 2>/dev/null | grep -c . || true; }
succeeded_count() { kubectl get pods -n "$TEST_NAMESPACE" -l "test=$1" --field-selector=status.phase=Succeeded --no-headers 2>/dev/null | grep -c . || true; }
all_logs() {
    local p
    for p in $(kubectl get pods -n "$TEST_NAMESPACE" -l "test=$1" -o name 2>/dev/null); do
        kubectl logs -n "$TEST_NAMESPACE" "$p" 2>/dev/null || true
    done
}
count_logs() { all_logs "$1" | grep -c -E "$2" || true; }

# wait_until <timeout seconds> <command...>: polls every 2s until the command succeeds.
wait_until() {
    local deadline=$((SECONDS + $1)); shift
    until "$@"; do
        [[ $SECONDS -ge $deadline ]] && return 1
        sleep 2
    done
}
at_least() { [[ "$($1 "$2")" -ge "$3" ]]; }
logs_at_least() { [[ "$(count_logs "$1" "$2")" -ge "$3" ]]; }

# Every item in first..last started exactly once and was acked with 200; every extend returned 200.
check_items_once() {
    local name="$1" first="$2" last="$3" logs n bad=""
    logs="$(all_logs "$name")"
    for n in $(seq "$first" "$last"); do
        [[ "$(grep -c "START n=${n} " <<< "$logs" || true)" == "1" ]] || bad+=" start(n=${n})"
        [[ "$(grep -c "ACK n=${n} status=200" <<< "$logs" || true)" == "1" ]] || bad+=" ack(n=${n})"
    done
    [[ -z "$bad" ]] && pass "${name}: items ${first}..${last} each started once and acked (200)" \
                    || fail "${name}: items ${first}..${last} each started once and acked (200)" "mismatched:${bad}"
    local extends failed_extends
    extends="$(grep -c 'EXTEND .*status=200' <<< "$logs" || true)"
    failed_extends="$(grep 'EXTEND' <<< "$logs" | grep -vc 'status=200' || true)"
    if [[ "$extends" -ge "$((last - first + 1))" && "$failed_extends" == "0" ]]; then
        pass "${name}: locks outlived the ${TTL}s TTL via ExtendLock (${extends} extends, all 200)"
    else
        fail "${name}: locks outlived the ${TTL}s TTL via ExtendLock" "${extends} successful, ${failed_extends} failed extends"
    fi
}

dump_on_failure() {
    [[ "$FAILED" == "true" ]] || return 0
    echo "--- pods ($1) ---"; kubectl get pods -n "$TEST_NAMESPACE" -l "test=$1" -o wide 2>/dev/null || true
    echo "--- logs ($1) ---"; all_logs "$1"
}

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

log_section "Setup"
kubectl get svc -n "$NAMESPACE" "${FULLNAME}-operator" >/dev/null \
    || { echo "No ${FULLNAME}-operator Service in ${NAMESPACE}: install DaprMQ with --set operator.enabled=true"; exit 1; }
kubectl get crd scaledjobs.keda.sh >/dev/null || { echo "KEDA is not installed (no scaledjobs.keda.sh CRD)"; exit 1; }
# A previous run's namespace may still be terminating (cleanup doesn't wait).
if [[ "$(kubectl get namespace "$TEST_NAMESPACE" -o jsonpath='{.status.phase}' 2>/dev/null)" == "Terminating" ]]; then
    kubectl wait --for=delete "namespace/$TEST_NAMESPACE" --timeout=180s >/dev/null
fi
kubectl create namespace "$TEST_NAMESPACE" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl create configmap scaledjob-test-worker -n "$TEST_NAMESPACE" --from-file=worker.py="$SCRIPT_DIR/worker.py" \
    --dry-run=client -o yaml | kubectl apply -f - >/dev/null
start_port_forward
log_info "scalerAddress=${SCALER_ADDRESS}  gateway=${GATEWAY_URL}  TTL=${TTL}s  work=${WORK_SECONDS}s  poll=${POLL}s"

# ---------------------------------------------------------------------------
# 1. One Job per item
# ---------------------------------------------------------------------------

log_section "1. One Job per item"
name="sj-per-item"; queue="keda-sj-per-item-${RUN_ID}"
apply_scaledjob "$name" "$queue" external
enqueue "$queue" 1 5
if wait_until 240 at_least succeeded_count "$name" 5; then
    sleep $((POLL * 3))   # a few more polls: no extra Jobs once the queue is empty
    check "${name}: exactly 5 Jobs for 5 items" "$(pod_count "$name")" 5
    check_items_once "$name" 1 5
else
    fail "${name}: 5 Jobs completed" "only $(succeeded_count "$name") succeeded within 240s"
fi
dump_on_failure "$name"; delete_scaledjob "$name"

# ---------------------------------------------------------------------------
# 2. In-flight work isn't counted twice
# ---------------------------------------------------------------------------

log_section "2. In-flight work isn't counted twice"
name="sj-in-flight"; queue="keda-sj-in-flight-${RUN_ID}"
apply_scaledjob "$name" "$queue" external
enqueue "$queue" 1 5
if wait_until 120 logs_at_least "$name" 'START n=' 5; then
    enqueue "$queue" 6 8
    if wait_until 240 at_least succeeded_count "$name" 8; then
        sleep $((POLL * 3))
        check "${name}: 5 in flight + 3 enqueued -> exactly 8 Jobs" "$(pod_count "$name")" 8
        check_items_once "$name" 1 8
    else
        fail "${name}: 8 Jobs completed" "only $(succeeded_count "$name") succeeded within 240s"
    fi
else
    fail "${name}: first 5 Jobs started" "only $(count_logs "$name" 'START n=') started within 120s"
fi
dump_on_failure "$name"; delete_scaledjob "$name"

# ---------------------------------------------------------------------------
# 3. A killed Job's item is redelivered
# ---------------------------------------------------------------------------

log_section "3. A killed Job's item is redelivered"
name="sj-killed"; queue="keda-sj-killed-${RUN_ID}"
apply_scaledjob "$name" "$queue" external
enqueue "$queue" 1 1
if wait_until 120 logs_at_least "$name" 'START n=1 ' 1; then
    victim="$(kubectl get pods -n "$TEST_NAMESPACE" -l "test=$name" -o name | head -n1)"
    kubectl delete -n "$TEST_NAMESPACE" "$victim" --grace-period=0 --force >/dev/null 2>&1
    log_info "killed ${victim} mid-work"
    if wait_until 180 logs_at_least "$name" 'ACK n=1 status=200' 1; then
        pass "${name}: item redelivered after its lock expired and completed by another Job"
        empty="$(count_logs "$name" 'EMPTY')"
        log_info "${empty} Job(s) found nothing while the dead Job's lock was still live (expected ~TTL/poll = $((TTL / POLL)))"
        check "${name}: item started once more after the kill" "$(count_logs "$name" 'START n=1 ')" 1
    else
        fail "${name}: item redelivered and completed" "no successful ACK within 180s"
    fi
else
    fail "${name}: Job started" "no START within 120s"
fi
dump_on_failure "$name"; delete_scaledjob "$name"

# ---------------------------------------------------------------------------
# 4. KEDA doesn't support external-push for ScaledJob (README tells users to use external).
# KEDA's first poll lands at an unpredictable time, so anchor on one: the first Job's creation marks
# a poll. An item enqueued right after it gets a Job within seconds with push, or only at the next
# poll (~push_poll later) without.
# ---------------------------------------------------------------------------

log_section "4. external-push is unsupported for ScaledJob"
name="sj-push"; queue="keda-sj-push-${RUN_ID}"; push_poll=60
apply_scaledjob "$name" "$queue" external-push "$push_poll"
enqueue "$queue" 1 1
if wait_until $((push_poll * 2 + 30)) at_least pod_count "$name" 1; then
    enqueue "$queue" 2 2
    enqueued_at=$SECONDS
    if wait_until $((push_poll + 30)) at_least pod_count "$name" 2; then
        waited=$((SECONDS - enqueued_at))
        if [[ $waited -ge $((push_poll * 2 / 3)) ]]; then
            pass "${name}: second Job waited for the next ${push_poll}s poll (${waited}s), not the push stream"
        else
            fail "${name}: second Job waited for the next poll" \
                "started ${waited}s after enqueue - KEDA may now support push for ScaledJob; update examples/keda/README.md"
        fi
    else
        fail "${name}: second Job started" "none within $((push_poll + 30))s"
    fi
else
    fail "${name}: first Job started" "none within $((push_poll * 2 + 30))s"
fi
dump_on_failure "$name"; delete_scaledjob "$name"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

log_section "Summary"
for r in "${RESULTS[@]}"; do
    if [[ "$r" == PASS* ]]; then echo -e "  ${GREEN}PASS${NC} ${r#PASS|}"; else echo -e "  ${RED}FAIL${NC} ${r#FAIL|}"; fi
done
[[ "$FAILED" == "true" ]] && exit 1
echo -e "${GREEN}All ScaledJob checks passed.${NC}"
