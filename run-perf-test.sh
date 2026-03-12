#!/bin/bash

# Run performance tests comparing HTTP and gRPC push operations
# Prerequisites: API server must be running with Dapr

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Parse command-line arguments
VIRTUAL_USERS=""
TEST_ITERATIONS=""
RAMPUP_RATE=""

function show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Run HTTP vs gRPC performance tests against the DaprMQ API.

OPTIONS:
    -u, --users NUM          Number of virtual users (concurrent clients per protocol)
                             Default: 1
    -i, --iterations NUM     Number of test iterations per virtual user
                             Default: 10000
    -r, --rampup RATE        Ramp-up rate in users per second
                             0 = instant (all start at once)
                             1.0 = 1 user per second
                             0.5 = 1 user every 2 seconds
                             Default: 1.0
    -h, --help              Show this help message

ENVIRONMENT VARIABLES:
    HTTP_ENDPOINT                    HTTP REST endpoint (default: http://localhost:8002)
    GRPC_ENDPOINT                    gRPC endpoint (default: http://localhost:8102)
    WARMUP_ITERATIONS                Warmup iterations per user (default: 10)
    RAMPUP_RATE_USERS_PER_SECOND     Ramp-up rate (default: 1.0, or 0 via -r flag)

EXAMPLES:
    # Run with default settings (1 user, 10000 iterations, 1.0 ramp-up rate)
    $0

    # Test with 10 concurrent users (gradual ramp-up over 9 seconds)
    $0 --users 10

    # Test with 10 users starting all at once (instant)
    $0 --users 10 --rampup 0

    # Quick test with 5 users ramping up at 2 users/second
    $0 -u 5 -i 1000 -r 2.0

    # Stress test with 50 users ramping up slowly (1 user every 2 seconds)
    $0 --users 50 --iterations 500 --rampup 0.5

PREREQUISITES:
    1. Docker Compose stack must be running: docker-compose up
    2. Gateway must be accessible on localhost:8002 (HTTP) and localhost:8102 (gRPC)

EOF
}

while [[ $# -gt 0 ]]; do
    case $1 in
        -u|--users)
            VIRTUAL_USERS="$2"
            shift 2
            ;;
        -i|--iterations)
            TEST_ITERATIONS="$2"
            shift 2
            ;;
        -r|--rampup)
            RAMPUP_RATE="$2"
            shift 2
            ;;
        -h|--help)
            show_usage
            exit 0
            ;;
        *)
            echo "Error: Unknown option: $1"
            echo "Run '$0 --help' for usage information."
            exit 1
            ;;
    esac
done

echo "=== DaprMQ Performance Tests ==="
echo ""
echo "Prerequisites:"
echo "  1. Docker Compose stack must be running"
echo "  2. Gateway must be accessible on localhost:8002 (HTTP) and localhost:8102 (gRPC)"
echo ""
echo "To start the Docker stack in another terminal:"
echo "  docker-compose up"
echo ""

# Determine which endpoint to check based on environment
HTTP_ENDPOINT="${HTTP_ENDPOINT:-http://localhost:8002}"
GRPC_ENDPOINT="${GRPC_ENDPOINT:-http://localhost:8102}"
HEALTH_URL="${HTTP_ENDPOINT}/health"

# Check if server is running
echo "Checking if gateway is running at ${HTTP_ENDPOINT}..."
if ! curl -s -f "${HEALTH_URL}" > /dev/null 2>&1; then
    echo "❌ Error: Gateway is not responding at ${HEALTH_URL}"
    echo "   Please start Docker Compose first: docker-compose up"
    exit 1
fi

echo "✅ Gateway is running"
echo ""

# Run performance tests
echo "Running performance tests..."
cd dotnet/tests/DaprMQ.PerformanceTests

# Build environment variable arguments
ENV_ARGS=""
if [ -n "$VIRTUAL_USERS" ]; then
    ENV_ARGS="VIRTUAL_USERS=$VIRTUAL_USERS $ENV_ARGS"
fi
if [ -n "$TEST_ITERATIONS" ]; then
    ENV_ARGS="TEST_ITERATIONS=$TEST_ITERATIONS $ENV_ARGS"
fi
# Default ramp-up rate is 1.0 (1 user per second)
if [ -n "$RAMPUP_RATE" ]; then
    ENV_ARGS="RAMPUP_RATE_USERS_PER_SECOND=$RAMPUP_RATE $ENV_ARGS"
else
    ENV_ARGS="RAMPUP_RATE_USERS_PER_SECOND=1.0 $ENV_ARGS"
fi

# Run with environment variables (ENV_ARGS is always set with at least ramp-up default)
env $ENV_ARGS dotnet run -c Release

echo ""
echo "Performance tests completed!"
