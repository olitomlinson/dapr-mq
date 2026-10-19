# In-cluster throughput test

Measures how fast competing consumers drain a backlog from one queue, entirely inside a Kubernetes cluster. The
publisher and the consumers are compiled .NET code ([ThroughputTest](ThroughputTest/Program.cs)) running as a Job
and a Deployment. Nothing is port-forwarded.

Results are records in the shared SDK harness schema ([result.schema.json](../../sdks/testing/perf/result.schema.json)),
so the perf report charts them alongside the SDK runs.

```bash
tools/k8s-throughput/run.sh --context kind-daprmq-e2e                    # the standard series: 2, 5, 10 and 20 replicas x 10 handlers
tools/k8s-throughput/run.sh --replicas 20                                # one run
tools/k8s-throughput/run.sh --replicas 20 --fixed                        # exactly 20, no autoscaling
tools/k8s-throughput/run.sh --replicas 80 --window 100 --handlers 1      # any other shape
dotnet run --project sdks/testing/perf/DaprMQ.PerfReport -- report --results perf-results   # -> perf-results/report.html
```

It needs a DaprMQ release with the operator's KEDA scaler (`operator.enabled`) and KEDA.
`k8s-base-deploy.sh` followed by `daprmq-deploy.sh` sets both up.

## What a run does

1. **Builds and loads the image.** `run.sh` builds `daprmq-throughput-test:dev` and, on a kind context, loads it
   into the cluster (`--skip-build` reuses it).
2. **Installs the chart** ([chart/](chart/)) into `daprmq-throughput` for each replica count in turn, with a fresh
   run id. The run id names the queue (`tp-<runId>`) and the Job, so every run starts on an empty queue.
3. **Starts the consumers.** They're a Deployment of `ThroughputTest consume`: a `QueueConsumer` with `--handlers`,
   `--window` (`MaxActiveMessages`), `--lock-ttl` and `--delay-ms` of simulated work per message. A KEDA
   ScaledObject scales them 0..`--replicas` from the queue's depth, as in production; `--fixed` runs exactly
   `--replicas`.
   - Every 5 seconds each consumer sends its cumulative stats to `tp-<runId>-stats`: messages handled, handlers
     busy, and a histogram of delivery latency (from the publish time stamped on each message to its handler
     starting).
4. **Runs the Job,** `ThroughputTest run`, which:
   - publishes `--messages` through the gateway, from `--publishers` concurrent publishers in batches of 100
   - polls the queue's depth (ready + locked) from the operator's KEDA scaler, the same endpoint KEDA reads,
     until the queue is empty
   - reads the stats queue, and prints the run as one `RESULT {json}` record
5. **Saves the record.** `run.sh` saves it where the harnesses save theirs:
   - `<out>/sdk-dotnet-k8s/runs/<runId>.json` holds the run in full
   - `<out>/sdk-dotnet-k8s/history.jsonl` gets the same record without its timeline

   It then prints a table of every run and uninstalls the release (`--keep` leaves the last one, to read
   consumer logs). It exits 0 only if every run passed its checks.

## The record

A run is a P-05 queue drain by the .NET SDK, at scale `extreme`, with profile `k8s-queue-drain-r<replicas>`.

- **Key:** `k8s-queue:<messages>@<delay>ms/active<window>/handlers<handlers>/replicas<replicas>`, with `+fixed`
  without KEDA. The report and regression check compare runs by `(environment.label, topology.apiReplicas,
  scenario.key)`, so a run is only ever compared with runs of the same shape on the same kind of machine. Set
  the label with `--env-label`; CI uses `ci-k8s-<runner>`.
- **Topology:** `apiReplicas` is the gateway's replica count, and `server` is `kubernetes <context>`.
- **Metrics:** P-05's, plus `deadLettered`, `peakDepth` and `peakReplicas`.

| Metric | Meaning |
|---|---|
| `wallClockSeconds` | From the first publish to the first of two consecutive empty depth reads. The scaler caches depth for a second, and an expired lock can put an item back after one empty read |
| `messagesPerSecond` | Messages handled ÷ wall clock |
| `idealSeconds`, `efficiency` | The handler work spread over every handler that can run (replicas × min(handlers, window)), and ideal ÷ wall clock. With one queue actor, expect this to fall as replicas rise |
| `deliveryLatencyMs` | Publish to handler start, merged from every consumer's histogram. Percentiles are within 10% |
| `peakConcurrentHandlers` | The most handlers busy at once across all consumers, from their 5-second reports |
| `messagesHandled`, `duplicates`, `missing` | From the consumers' counts. Redeliveries count as handled again |
| `deadLettered` | What reached `tp-<runId>-deadletter`. It should be 0: a non-zero count usually means a window too large for the handlers |

- **Timeline:** `messagesPerSecond` and `busyHandlers` in 5-second buckets.
- **Checks** fail if the queue didn't drain, if messages are missing, or if any were dead-lettered.

These profiles aren't in [profiles.json](../../sdks/testing/perf/profiles.json) or any harness suite: only
this test produces them.

## In CI

The [Performance (extreme)](../../.github/workflows/perf-extreme.yml) workflow runs it in its `k8s-throughput` job:
- it creates a kind cluster on the runner and deploys the stack
- it runs `--replicas` from the `k8s_replicas` input (default `2,5,10,20`, the standard series)
- it writes the table to the job summary
- it uploads the records as `perf-sdk-dotnet-k8s`, which the publish job merges into the perf-results branch

A standard runner has 4 vCPUs shared by every pod, so the larger replica counts can measure the runner as much
as DaprMQ. Use the workflow's `runner` input for a larger one.
