#!/usr/bin/env bash
# In-cluster throughput runs: competing consumers (a Deployment, KEDA-scaled by default) drain a
# backlog that a Job publishes and times. Both are the compiled ThroughputTest app; nothing is
# port-forwarded - the Job talks to the gateway and the operator's scaler inside the cluster.
# --workload sessions spreads the backlog over sessions, drained by session consumers instead.
#
# Each run's result is a record in the shared SDK harness schema (sdks/testing/perf/result.schema.json),
# a P-05 queue drain with profile k8s-queue-drain-r<replicas> (or for sessions, a P-04 session drain with
# profile k8s-session-drain-r<replicas>), saved where the harnesses save theirs:
# <out>/sdk-dotnet-k8s/runs/<runId>.json and a line in <out>/sdk-dotnet-k8s/history.jsonl. So
# `DaprMQ.PerfReport report` charts it with the rest, and CI publishes it to the perf-results branch.
#
# Every run gets a fresh queue (tp-<runId>), so runs need no reset between them.
#
# Usage:
#   tools/k8s-throughput/run.sh [options]
#
# Options:
#   --context NAME        kube context (default: current)
#   --namespace NS        namespace for the test (default: daprmq-throughput)
#   --daprmq-namespace NS namespace of the DaprMQ release (default: daprmq)
#   --replicas N[,N...]   max consumer replicas (or the exact count with --fixed); a list runs each in
#                         turn (default: 2,5,10,20, the standard series)
#   --fixed               run exactly --replicas consumers instead of scaling with KEDA
#   --workload NAME       queue (default) or sessions
#   --handlers N          handlers per consumer (default: 10)
#   --window N            MaxActiveMessages per consumer (default: 40)
#   --delay-ms N          simulated work per message (default: 200)
#   --lock-ttl N          lock TTL in seconds (default: 60)
#   --sessions N          sessions workload: sessions the backlog is spread over; must divide --messages (default: 1000)
#   --concurrent-sessions N  sessions workload: sessions each consumer holds at once (default: 10)
#   --prefetch N          sessions workload: messages in flight per session (default: 1)
#   --lease N             sessions workload: session lease in seconds (default: 30)
#   --session-idle-timeout N  sessions workload: seconds a slot waits on an emptied session (default: 1)
#   --session-length-jitter N  sessions workload: draw each session's length within N% of the mean, 0-90 (default: 0)
#   --publish-jitter N    sessions workload: publish each session at a random time in the first N seconds (default: 0)
#   --messages N          backlog size (default: 100000)
#   --publishers N        concurrent publishers in the Job (default: 10)
#   --timeout N           seconds to wait for each drain (default: 1800)
#   --env-label NAME      the result's environment.label; runs are only compared within one (default: local)
#   --out DIR             results root (default: <repo>/perf-results)
#   --skip-build          use the daprmq-throughput-test:dev image already built
#   --keep                leave the last run's release installed (to read consumer logs)
#   -h, --help            show this help

set -euo pipefail

CONTEXT=""; NS="daprmq-throughput"; DAPRMQ_NS="daprmq"; REPLICAS_LIST="2,5,10,20"; MODE="keda"; WORKLOAD="queue"
HANDLERS=10; WINDOW=40; DELAY_MS=200; LOCK_TTL=60; MESSAGES=100000; PUBLISHERS=10; TIMEOUT=1800
SESSIONS=1000; CONCURRENT_SESSIONS=10; PREFETCH=1; LEASE=30; SESSION_IDLE_TIMEOUT=1; LENGTH_JITTER=0; PUBLISH_JITTER=0
ENV_LABEL="local"; OUT=""; SKIP_BUILD="false"; KEEP="false"
IMAGE="daprmq-throughput-test:dev"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHART="$REPO_ROOT/tools/k8s-throughput/chart"

show_help() { sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --context) CONTEXT="$2"; shift 2 ;;
        --namespace) NS="$2"; shift 2 ;;
        --daprmq-namespace) DAPRMQ_NS="$2"; shift 2 ;;
        --replicas) REPLICAS_LIST="$2"; shift 2 ;;
        --fixed) MODE="fixed"; shift ;;
        --workload) WORKLOAD="$2"; shift 2 ;;
        --sessions) SESSIONS="$2"; shift 2 ;;
        --concurrent-sessions) CONCURRENT_SESSIONS="$2"; shift 2 ;;
        --prefetch) PREFETCH="$2"; shift 2 ;;
        --lease) LEASE="$2"; shift 2 ;;
        --session-idle-timeout) SESSION_IDLE_TIMEOUT="$2"; shift 2 ;;
        --session-length-jitter) LENGTH_JITTER="$2"; shift 2 ;;
        --publish-jitter) PUBLISH_JITTER="$2"; shift 2 ;;
        --handlers) HANDLERS="$2"; shift 2 ;;
        --window) WINDOW="$2"; shift 2 ;;
        --delay-ms) DELAY_MS="$2"; shift 2 ;;
        --lock-ttl) LOCK_TTL="$2"; shift 2 ;;
        --messages) MESSAGES="$2"; shift 2 ;;
        --publishers) PUBLISHERS="$2"; shift 2 ;;
        --timeout) TIMEOUT="$2"; shift 2 ;;
        --env-label) ENV_LABEL="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        --skip-build) SKIP_BUILD="true"; shift ;;
        --keep) KEEP="true"; shift ;;
        -h|--help) show_help; exit 0 ;;
        *) echo "Unknown option: $1" >&2; show_help >&2; exit 1 ;;
    esac
done

case "$WORKLOAD" in
    queue|sessions) ;;
    *) echo "--workload must be queue or sessions, got: $WORKLOAD" >&2; exit 1 ;;
esac

CONTEXT="${CONTEXT:-$(kubectl config current-context)}"
OUT="${OUT:-$REPO_ROOT/perf-results}"
RESULTS_DIR="$OUT/sdk-dotnet-k8s"
k() { kubectl --context "$CONTEXT" -n "$NS" "$@"; }

if [[ "$SKIP_BUILD" != "true" ]]; then
    echo "== building $IMAGE"
    docker build -q -t "$IMAGE" -f "$REPO_ROOT/tools/k8s-throughput/Dockerfile" "$REPO_ROOT" >/dev/null
    if [[ "$CONTEXT" == kind-* ]]; then
        kind load docker-image --name "${CONTEXT#kind-}" "$IMAGE" >/dev/null
    fi
fi

# What the record needs to say about where it ran.
GATEWAY_REPLICAS="$(kubectl --context "$CONTEXT" -n "$DAPRMQ_NS" get deploy -l app.kubernetes.io/component=gateway \
    -o jsonpath='{.items[0].spec.replicas}' 2>/dev/null || true)"
GATEWAY_REPLICAS="${GATEWAY_REPLICAS:-1}"
GIT_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
GIT_BRANCH="${GITHUB_HEAD_REF:-${GITHUB_REF_NAME:-$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || true)}}"
GIT_DIRTY="false"; [[ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]] && GIT_DIRTY="true"

uninstall() { helm --kube-context "$CONTEXT" uninstall throughput -n "$NS" --wait >/dev/null 2>&1 || true; }
trap '[[ "$KEEP" == "true" ]] || uninstall' EXIT

# run_one <replicas>: installs one run, waits for its Job, saves its record. Prints the record's path.
run_one() {
    local replicas="$1" run_id job peak=0 deadline ready succeeded failed record
    run_id="$(date -u +%Y%m%d%H%M%S)"
    job="tp-$run_id"
    if [[ "$WORKLOAD" == "sessions" ]]; then
        echo "== run $run_id: $MESSAGES messages over $SESSIONS sessions, $MODE session consumers (up to $replicas) x $CONCURRENT_SESSIONS sessions, prefetch $PREFETCH, ${DELAY_MS}ms per message, lease ${LEASE}s, idle timeout ${SESSION_IDLE_TIMEOUT}s, length jitter ${LENGTH_JITTER}%, publish jitter ${PUBLISH_JITTER}s" >&2
    else
        echo "== run $run_id: $MESSAGES messages, $MODE consumers (up to $replicas) x $HANDLERS handlers, window $WINDOW, ${DELAY_MS}ms per message, lock TTL ${LOCK_TTL}s" >&2
    fi
    uninstall
    helm --kube-context "$CONTEXT" upgrade --install throughput "$CHART" -n "$NS" --create-namespace \
        --set-string runId="$run_id" --set daprmq.namespace="$DAPRMQ_NS" --set workload="$WORKLOAD" \
        --set scaling.mode="$MODE" --set scaling.maxReplicas="$replicas" --set scaling.replicas="$replicas" \
        --set consumer.handlers="$HANDLERS" --set consumer.window="$WINDOW" \
        --set consumer.itemDelayMs="$DELAY_MS" --set consumer.lockTtlSeconds="$LOCK_TTL" \
        --set sessions.count="$SESSIONS" --set sessions.concurrent="$CONCURRENT_SESSIONS" --set sessions.prefetch="$PREFETCH" \
        --set sessions.leaseSeconds="$LEASE" --set sessions.idleTimeoutSeconds="$SESSION_IDLE_TIMEOUT" \
        --set sessions.lengthJitterPercent="$LENGTH_JITTER" --set sessions.publishJitterSeconds="$PUBLISH_JITTER" \
        --set run.messages="$MESSAGES" --set run.publishers="$PUBLISHERS" --set run.timeoutSeconds="$TIMEOUT" \
        --set-string run.envLabel="$ENV_LABEL" --set run.gatewayReplicas="$GATEWAY_REPLICAS" \
        --set-string run.serverLabel="kubernetes $CONTEXT" --set-string run.gitSha="$GIT_SHA" \
        --set-string run.gitBranch="$GIT_BRANCH" --set-string run.gitDirty="$GIT_DIRTY" >/dev/null

    # Wait for the Job, noting the most consumer replicas seen (the Job can't see replica counts itself).
    deadline=$((SECONDS + TIMEOUT + 360))
    while :; do
        ready="$(k get deploy throughput-consumer -o jsonpath='{.status.readyReplicas}' 2>/dev/null || true)"
        (( ${ready:-0} > peak )) && peak=${ready:-0}
        succeeded="$(k get job "$job" -o jsonpath='{.status.succeeded}' 2>/dev/null || true)"
        failed="$(k get job "$job" -o jsonpath='{.status.failed}' 2>/dev/null || true)"
        [[ "${succeeded:-0}" -ge 1 || "${failed:-0}" -ge 1 ]] && break
        if (( SECONDS > deadline )); then echo "!! gave up waiting for job $job" >&2; break; fi
        sleep 5
    done

    k logs "job/$job" | grep -v '^RESULT ' | sed 's/^/   /' >&2
    record="$(k logs "job/$job" | sed -n 's/^RESULT //p' | tail -1)"
    if [[ -z "$record" ]]; then
        echo "!! run $run_id: no result record - the run failed before measuring" >&2
        return 1
    fi

    # Saved exactly as the harnesses' PerfResults.Save does: the run in full, and a history line without its timeline.
    python3 - "$record" "$RESULTS_DIR" "$peak" <<'PY'
import json, os, sys
run = json.loads(sys.argv[1]); out = sys.argv[2]
run["metrics"]["peakReplicas"] = int(sys.argv[3])
os.makedirs(os.path.join(out, "runs"), exist_ok=True)
path = os.path.join(out, "runs", run["runId"] + ".json")
with open(path, "w") as f:
    json.dump(run, f, indent=2)
summary = {k: v for k, v in run.items() if k != "timeline"}
with open(os.path.join(out, "history.jsonl"), "a") as f:
    f.write(json.dumps(summary, separators=(",", ":")) + "\n")
print(path)
PY
}

IFS=',' read -r -a REPLICAS <<< "$REPLICAS_LIST"
PATHS=()
status=0
for replicas in "${REPLICAS[@]}"; do
    if path="$(run_one "$replicas")"; then
        PATHS+=("$path")
    else
        status=1
    fi
done

echo "== results ($RESULTS_DIR)"
python3 - "${PATHS[@]}" <<'PY'
import json, sys
def f(v, d=1): return "-" if v is None else f"{v:,.{d}f}"
# Sessions records have no dead-letter count (each session has its own DLQ) but do count FIFO violations.
print(f"   {'profile':<24} {'drained':<8} {'wall s':>8} {'msg/s':>7} {'efficiency':>10} {'delivery p95 ms':>15} {'peak handlers':>13} {'dead-lettered':>13} {'fifo':>5}  checks")
for path in sys.argv[1:]:
    r = json.load(open(path)); m = r["metrics"]; failures = r["checks"]["failures"]
    checks = "pass" if r["checks"]["passed"] else "FAIL: " + "; ".join(failures)
    drained = not any(f_.endswith("did not drain") for f_ in failures)
    print(f"   {r['scenario']['profile']:<24} {str(drained):<8} {f(m['wallClockSeconds']):>8} "
          f"{f(m['messagesPerSecond'], 0):>7} {f(m['efficiency'], 2):>10} {f(m['deliveryLatencyMs']['p95'], 0):>15} "
          f"{m['peakConcurrentHandlers']:>13} {f(m.get('deadLettered'), 0):>13} {f(m.get('fifoViolations'), 0):>5}  {checks}")
PY
for path in "${PATHS[@]}"; do
    python3 -c 'import json,sys; sys.exit(0 if json.load(open(sys.argv[1]))["checks"]["passed"] else 1)' "$path" || status=1
done
exit $status
