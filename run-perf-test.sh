#!/bin/bash

# .NET SDK performance harness (sdks/dotnet/perf): the profiles of sdks/testing/PERFORMANCE_TESTS.md
# against a Testcontainers stack it starts itself (3-node scheduler HA; --api-replicas N adds N API
# servers behind nginx), unless --http/--grpc point at an existing server.
# Results go to perf-results/sdk-dotnet; perf-results/report.html compares every SDK found there.
#
# Examples:
#   ./run-perf-test.sh --suite pr                       # every pr profile, ~6 min
#   ./run-perf-test.sh --profile enqueue                # one profile, ~1 min
#   ./run-perf-test.sh --suite extreme                  # ramps + big drains on 3 replicas, ~2h
#   ./run-perf-test.sh --profile enqueue-ramp --api-replicas 5
#   ./run-perf-test.sh --profile steady-drain --idle-timeout 2
#   ./run-perf-test.sh --http http://localhost:8002 --grpc http://localhost:8102 --profile enqueue
#   ./run-perf-test.sh --report                         # only regenerate the report
#   ./run-perf-test.sh --benchmark state-reads          # actor state reads/writes per operation, ~2 min

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if [[ " $* " == *" --help "* || " $* " == *" -h "* ]]; then
    sed -n '3,18p' "$0" | sed 's/^# \{0,1\}//'
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
