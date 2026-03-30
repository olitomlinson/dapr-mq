# DaprMQ Performance Tests

Manual performance testing tool to compare HTTP and gRPC push operation latencies.

## Prerequisites

1. Start the Docker Compose stack:
   ```bash
   docker-compose up
   ```

2. The gateway will listen on:
   - HTTP (REST): `http://localhost:8002`
   - gRPC: `http://localhost:8102`

Alternatively, for local development without Docker:
   ```bash
   cd ../../src/DaprMQ.ApiServer
   dapr run --app-id daprmq-api \
     --app-port 5000 \
     --resources-path ../../../dapr/components \
     -- dotnet run
   ```
   Then override endpoints:
   ```bash
   HTTP_ENDPOINT=http://localhost:5000 GRPC_ENDPOINT=http://localhost:5001 dotnet run
   ```

## Running the Tests

### Using the Convenience Script (Recommended)

```bash
# Run with default settings (1 user, 10000 iterations)
./run-perf-test.sh

# Test with 10 concurrent users
./run-perf-test.sh --users 10

# Quick test with 5 users and 1000 iterations each
./run-perf-test.sh -u 5 -i 1000

# Stress test with 50 users
./run-perf-test.sh --users 50 --iterations 500

# Show help
./run-perf-test.sh --help
```

### Using dotnet run directly

```bash
# From this directory
dotnet run

# Or from project root
dotnet run --project dotnet/tests/DaprMQ.PerformanceTests
```

## Test Configuration

- **Virtual users**: 1 (concurrent clients per protocol, configurable)
- **Warmup iterations**: 10 per virtual user (to warm up JIT, connections, etc.)
- **Test iterations**: 10000 per virtual user (for statistical significance and longer time-series data)
- **Total operations**: `virtual_users × test_iterations` per protocol
- **Queue IDs**: Randomized on each run to ensure test isolation
  - **Each virtual user gets its own queue**
  - HTTP: `perf-test-http-{randomId}-user{N}` where N = 0 to virtualUsers-1
  - gRPC: `perf-test-grpc-{randomId}-user{N}` where N = 0 to virtualUsers-1
- **Test payload**: `{"id":1,"name":"performance-test","timestamp":"..."}`

Each test run uses **separate queue IDs** for HTTP and gRPC to prevent cross-contamination. Additionally, each virtual user operates on its own queue to eliminate queue-level contention and simulate realistic multi-tenant scenarios.

### Virtual Users

Virtual users simulate concurrent clients hitting the API in parallel. Each virtual user:
- Runs independently with its own HTTP client / gRPC channel
- **Has its own unique queue** (no queue contention between users)
- Executes `test_iterations` operations sequentially
- Operates in parallel with other virtual users

All timing data from all virtual users is **aggregated** at the end to calculate overall statistics (mean, P95, etc.) and generate time-series graphs.

**Example**: With `VIRTUAL_USERS=5` and `TEST_ITERATIONS=1000`:
- 5 HTTP clients run concurrently, each with its own queue, each doing 1000 push operations = 5000 total HTTP operations
- 5 gRPC clients run concurrently, each with its own queue, each doing 1000 push operations = 5000 total gRPC operations
- Queue IDs: `perf-test-http-{runId}-user0` through `user4` (and same for gRPC)
- Final stats aggregate all 5000 data points per protocol

This ensures no queue-level contention - each virtual user operates on an isolated queue, simulating realistic multi-tenant scenarios.

## Expected Results

gRPC should have **similar or better** performance than HTTP:
- ✅ Expected: gRPC mean latency ≤ HTTP mean latency
- ⚠️ Problem: gRPC mean latency >> HTTP mean latency (e.g., 50-70ms vs 3-5ms)

## Metrics Reported

- **Min/Max**: Fastest and slowest request
- **Mean**: Average latency
- **Median**: 50th percentile (P50)
- **P95**: 95th percentile (only 5% of requests slower)
- **P99**: 99th percentile (only 1% of requests slower)

## Visual Output

The test automatically generates a **time-based performance graph** with 1-second bucketing:

**X-Axis**: Time in seconds (normalized to T=0 at start of each test)
**Y-Axis**: Latency in milliseconds

**Graph Series**:
- **Solid Blue Line**: HTTP Average latency per second
- **Dashed Blue Line**: HTTP P95 latency per second
- **Solid Green Line**: gRPC Average latency per second
- **Dashed Green Line**: gRPC P95 latency per second

**Output file**: `results/performance-results_YYYY-MM-DD_HH-mm-ss.png`

Results are saved to the `results/` directory with timestamped filenames for easy browsing and comparison across multiple test runs.

The graph helps visualize:
- **Latency trends over time**: See how performance changes as the test progresses
- **Average vs P95 spread**: Understand variance and tail latency
- **Comparative performance**: Direct visual comparison between HTTP and gRPC
- **Time-based patterns**: Identify warmup effects, degradation, or stability over time
- **Bucket aggregation**: Each second of test execution is analyzed independently

## Troubleshooting Slow gRPC Performance

If gRPC is significantly slower than HTTP, check:

1. **HTTP/2 configuration**: Verify gRPC port (5001) is using `HttpProtocols.Http2`
2. **Connection reuse**: Ensure `GrpcChannel` is reused (not created per request)
3. **DNS resolution**: Check if gRPC client is doing DNS lookups on each request
4. **TLS negotiation**: Verify h2c (HTTP/2 cleartext) is properly configured
5. **Dapr sidecar**: Check Dapr logs for any gRPC-specific issues
6. **Network inspection**: Use Wireshark to compare HTTP vs gRPC network packets

## Configuration

You can override the default endpoints and test parameters via environment variables:
- `HTTP_ENDPOINT` - HTTP REST endpoint (default: `http://localhost:8002`)
- `GRPC_ENDPOINT` - gRPC endpoint (default: `http://localhost:8102`)
- `VIRTUAL_USERS` - Number of concurrent virtual users per protocol (default: `1`)
- `WARMUP_ITERATIONS` - Number of warmup iterations per user (default: `10`)
- `TEST_ITERATIONS` - Number of test iterations per user (default: `10000`)

**Note**: Queue IDs are automatically generated with random suffixes on each run. This cannot be overridden as it ensures test isolation.

### Using Environment Variables with dotnet run

```bash
# Test with 10 concurrent users
VIRTUAL_USERS=10 dotnet run

# Quick test with fewer iterations per user
VIRTUAL_USERS=5 TEST_ITERATIONS=1000 dotnet run

# Test against local development server with high concurrency
HTTP_ENDPOINT=http://localhost:5000 GRPC_ENDPOINT=http://localhost:5001 VIRTUAL_USERS=20 dotnet run
```

### Using the Shell Script

The `run-perf-test.sh` script provides a convenient command-line interface:

```bash
./run-perf-test.sh [OPTIONS]

OPTIONS:
    -u, --users NUM          Number of virtual users (default: 1)
    -i, --iterations NUM     Number of test iterations per user (default: 10000)
    -h, --help              Show help message

# Examples:
./run-perf-test.sh --users 10
./run-perf-test.sh -u 5 -i 1000
./run-perf-test.sh --help
```

## Example Output

```
=== Push Operation Performance Test ===
HTTP Endpoint: http://localhost:8002
gRPC Endpoint: http://localhost:8102
Virtual users: 5 (each with own queue)
HTTP Queue ID pattern: perf-test-http-a3f8d42e-user[0-4]
gRPC Queue ID pattern: perf-test-grpc-a3f8d42e-user[0-4]
Warmup iterations: 10 (per virtual user)
Test iterations: 1000 (per virtual user)
Total operations: 5000 (per protocol)

Warming up...
Warmup complete. Starting performance tests...

Testing HTTP Push (5 virtual users × 1000 iterations = 5000 total operations)...
Testing gRPC Push (5 virtual users × 1000 iterations = 5000 total operations)...

=== RESULTS ===
(Aggregated across 5 virtual users)

HTTP Push:
  Total operations: 5000
  Min:              2.75 ms
  Max:              10.70 ms
  Mean:             4.21 ms
  Median:           3.65 ms
  P95:              7.90 ms
  P99:              10.70 ms

gRPC Push:
  Total operations: 5000
  Min:              2.81 ms
  Max:              10.22 ms
  Mean:             3.54 ms
  Median:           3.40 ms
  P95:              4.96 ms
  P99:              10.22 ms

Comparison:
  ✅ gRPC is 1.19x FASTER than HTTP

Generating performance graph...
  📊 Graph saved to: /path/to/results/performance-results_2026-03-08_14-30-45.png

Performance tests completed!
```
