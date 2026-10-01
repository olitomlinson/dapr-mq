#!/bin/bash

# End-to-end session drain benchmark: .NET SDK SessionQueueConsumer against a real server.
# Starts its own Testcontainers stack unless --http/--grpc point at an existing one.
# Results accumulate in sdks/dotnet/perf/results (history.jsonl + runs/), report.html is regenerated.
#
# Examples:
#   ./run-session-perf-test.sh                          # full: 1000 sessions x 100 msgs x 1s, 20 slots (~1.5h+)
#   ./run-session-perf-test.sh --profile quick          # 100 x 20, ~5 min
#   ./run-session-perf-test.sh --suite ci               # the five CI smoke profiles, ~4 min
#   ./run-session-perf-test.sh --profile quick --idle-timeout 1
#   ./run-session-perf-test.sh --http http://localhost:8002 --grpc http://localhost:8102
#   ./run-session-perf-test.sh --report                 # only regenerate the report

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if [[ " $* " == *" --help "* || " $* " == *" -h "* ]]; then
    sed -n '3,14p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
fi

if [[ " $* " != *" --http "* && " $* " != *" --report "* ]]; then
    IMAGE="${DAPRMQ_API_IMAGE:-daprmq-api:test}"
    if ! docker image inspect "$IMAGE" > /dev/null 2>&1; then
        echo "❌ Image $IMAGE not found. Build it first: ./build-and-test.sh --skip-tests"
        exit 1
    fi
fi

dotnet run -c Release --project sdks/dotnet/perf/DaprMQ.Client.Perf -- "$@"
