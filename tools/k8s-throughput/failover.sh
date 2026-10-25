#!/usr/bin/env bash
# A session drain during which the worker pod hosting the queue's SessionCoordinatorActor is deleted
# (a graceful stop, as in a rolling restart). Passes only if run.sh's own checks pass after it: every
# message handled, in order. That is the "an enqueue that returned 200 is processed" guarantee,
# across a coordinator move.
set -euo pipefail

TOOL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<'EOF'
Usage: tools/k8s-throughput/failover.sh [options] [run.sh options...]

Runs tools/k8s-throughput/run.sh --workload sessions with the given options, and part-way through
deletes the worker pod hosting the queue's SessionCoordinatorActor. Exits non-zero if the pod
couldn't be found or deleted, or if run.sh's checks fail (not drained, or FIFO violated).

Options:
  --context NAME          kube context (passed to run.sh too; default: current)
  --daprmq-namespace NS   namespace of the DaprMQ release (passed to run.sh too; default: daprmq)
  --kill-after N          seconds after claims start flowing before deleting the coordinator's host
                          (default: 30)
  -h, --help              show this help

Any other option goes to run.sh, for example --replicas 10 --sessions 10000 --fast-scale-up.
EOF
}

CONTEXT=""
NAMESPACE="daprmq"
KILL_AFTER=30
RUN_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --context) CONTEXT="$2"; RUN_ARGS+=(--context "$2"); shift 2 ;;
    --daprmq-namespace) NAMESPACE="$2"; RUN_ARGS+=(--daprmq-namespace "$2"); shift 2 ;;
    --kill-after) KILL_AFTER="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) RUN_ARGS+=("$1"); shift ;;
  esac
done

k() { kubectl ${CONTEXT:+--context "$CONTEXT"} -n "$NAMESPACE" "$@"; }

# The worker hosting the coordinator is the one whose daprd hands claims (AcceptSession,
# ReleaseAndAcceptSession) to its app: the one whose count of them rises over a 10s window. Read from
# daprd's metrics through the API server's pod proxy, so nothing depends on log levels or rotation.
claims() {
  kubectl ${CONTEXT:+--context "$CONTEXT"} get --raw "/api/v1/namespaces/$NAMESPACE/pods/${1#pod/}:9090/proxy/metrics" 2>/dev/null \
    | awk '/^dapr_http_client_completed_count/ && /actors\/SessionCoordinatorActor\/\{id\}\/method\/(AcceptSession|ReleaseAndAcceptSession)"/ { sum += $NF }
           END { printf "%d\n", sum }'
}

coordinator_host() {
  local pods=() before=() best="" best_delta=0 delta i
  read -r -a pods <<< "$(k get pods -l app.kubernetes.io/component=worker -o name | tr '\n' ' ')"
  for i in "${!pods[@]}"; do before[i]=$(claims "${pods[i]}"); done
  sleep 10
  for i in "${!pods[@]}"; do
    delta=$(( $(claims "${pods[i]}") - before[i] ))
    if (( delta > best_delta )); then best="${pods[i]}"; best_delta=$delta; fi
  done
  echo "$best"
}

# Stops run.sh and what it started, so giving up never leaves a run going on the cluster. run.sh
# uninstalls its release on the way out.
stop_run() {
  pkill -TERM -P "$run_pid" 2>/dev/null || true
  kill -TERM "$run_pid" 2>/dev/null || true
  wait "$run_pid" 2>/dev/null || true
}

"$TOOL_DIR/run.sh" --workload sessions "${RUN_ARGS[@]}" &
run_pid=$!

# Building and loading the image can take minutes, so time the kill from the drain, not the start:
# wait for claims to start flowing (run.sh's own output is buffered when piped, so it can't tell us).
host=""
for _ in $(seq 1 180); do
  if ! kill -0 "$run_pid" 2>/dev/null; then
    status=0; wait "$run_pid" || status=$?
    echo "failover: run.sh ended (exit $status) before any claims were seen; nothing deleted" >&2
    exit 1
  fi
  host=$(coordinator_host)
  [[ -n "$host" ]] && break
done

if [[ -n "$host" ]]; then
  echo "failover: $(date +%T) claims are flowing; deleting the coordinator's host in ${KILL_AFTER}s"
  sleep "$KILL_AFTER"
  host=$(coordinator_host) # it may have moved on its own; delete wherever it is now
fi

if [[ -z "$host" ]]; then
  echo "failover: no worker's daprd handed the coordinator any claims within 30 minutes; nothing deleted" >&2
  stop_run
  exit 1
fi

echo "failover: $(date +%T) deleting $host, the SessionCoordinatorActor's host"
k delete "$host" --wait=false

status=0
wait "$run_pid" || status=$?
echo "failover: run.sh exited $status after the coordinator's host was deleted"
exit $status
