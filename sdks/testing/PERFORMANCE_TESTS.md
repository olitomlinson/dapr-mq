# SDK Performance Test Matrix

Source of truth for the performance scenarios every DaprMQ client SDK runs against a real server. Every
SDK runs the same scenarios with the same parameters and writes the same result format, so one report
can put the SDKs side by side.

**Rules**
- Every scenario has a stable ID (`P-01`…) and named **profiles** (fixed parameter sets). A profile
  means the same thing in every SDK. If you change a profile's parameters, rename the profile so old
  history isn't compared with new.
- Scenarios drive the **public SDK surface** (the client, `SessionQueueConsumer` and `QueueConsumer`), never raw
  HTTP/gRPC. Users only see the protocol through an SDK.
- Each run uses fresh queue IDs (`perf-<scenario>-<guid>`). Never include `-session-` in a base
  queue ID, because the server treats that marker as a per-session actor.
- The harness process both publishes and consumes, so `publishedAt` (Unix ms, in the payload) is
  read from the same clock at both ends.
- Tick a box only when the profile exists **and** passes locally and in CI.

## Scales and topology

| Scale | Runs | API replicas | Scheduler | Load balancer | Purpose |
|---|---|---|---|---|---|
| `pr` | every PR / push to `main` touching `server/`, that SDK, or `sdks/testing/` (plus nightly) | 1 | 3-node HA | none | Small and quick: catch regressions in SDK and server overhead |
| `extreme` | only manually ([perf-extreme.yml](../../.github/workflows/perf-extreme.yml)) | 3 (`--api-replicas N`) | 3-node HA | nginx (REST + gRPC) | Find where throughput stops scaling, compare SDKs under saturation |

Each SDK's own Testcontainers fixture builds the perf stack. It's the integration stack
([INTEGRATION_TESTS.md](INTEGRATION_TESTS.md)) with these changes:

- **Scheduler HA.** Three `ghcr.io/dapr/dapr:1.18.4` containers, aliases `dapr-scheduler-0..2`, each run as
  `./scheduler --port 50006 --id dapr-scheduler-<i> --etcd-initial-cluster dapr-scheduler-0=http://dapr-scheduler-0:2380,dapr-scheduler-1=http://dapr-scheduler-1:2380,dapr-scheduler-2=http://dapr-scheduler-2:2380 --etcd-client-listen-address 0.0.0.0 --etcd-data-dir /tmp/etcd --override-broadcast-host-port dapr-scheduler-<i>:50006`.
  Every daprd gets `--scheduler-host-address dapr-scheduler-0:50006,dapr-scheduler-1:50006,dapr-scheduler-2:50006`.
  This matches production. Placement stays as one container, since Dapr 1.19 folds it into the scheduler.
- **API replicas.** Replica *i* is `api-server-<i>` with its own sidecar `dapr-sidecar-<i>`
  (`--app-channel-address api-server-<i>`, same `--app-id daprmq-api`). The API server's
  `DAPR_HTTP_ENDPOINT`/`DAPR_GRPC_ENDPOINT` point at its own sidecar. Dapr spreads queue actors across
  the replicas.
- **Load balancer** (whenever replicas > 1). `public.ecr.aws/docker/library/nginx:1.27-alpine`, alias `api-lb`. Port 5000 proxies
  REST and port 5001 (`listen 5001 http2`) uses `grpc_pass` to every replica. Read/send timeouts are
  1 h so `ConsumeSession` streams survive. The SDK connects to the LB, and waits for the stack by polling a
  probe enqueue through it.

The scheduler isn't on DaprMQ's hot paths (lock expiry has no reminders), so runs are grouped and
compared by API replica count only. The scheduler replica count is recorded but not part of the key.

## Scenarios

### Closed-loop load (`P-01`–`P-03`)

`concurrency` workers each run the operation in a loop, back to back, for `warmupSeconds` (not
recorded) and then `durationSeconds` (recorded). Worker *w* uses queue `w % queues`. Each worker runs
in its own task, coroutine or thread, depending on the language. Use one shared client per process,
since that's how the SDKs are meant to be used.

| ID | Name | One operation | Messages per op |
|---|---|---|---|
| P-01 | `enqueue` | `enqueue(queue, [item])` | 1 |
| P-02 | `enqueue-batch` | `enqueue(queue, [batchSize items])` | `batchSize` |
| P-03 | `dequeue-ack` | `dequeueLocked(queue, count = dequeueCount)`, then `acknowledge` every returned lock | items returned |

**Item payload:** `{"worker": w, "seq": n, "publishedAt": <unix ms>, "pad": "<payloadBytes × 'x'>"}`, priority 1, no session.

**P-03 seeding (untimed):** before the clock starts, each queue gets `seedPerQueue` items via
`enqueue` in batches of 100. A worker that finds its queue empty stops, and the run records
`checks.drained = true`. That means the seed was too small, so the run fails.

**Ramp (`extreme` only):** the same loop, stepping `concurrency` through `rampSteps`. Each step runs
for `durationSeconds` after its own `warmupSeconds`, on fresh queues, with `queues = concurrency`.
This gives the throughput-vs-concurrency and latency-vs-concurrency curves.

| Profile | Scale | Scenario | concurrency | queues | batchSize | dequeueCount | payloadBytes | seedPerQueue | warmup / duration |
|---|---|---|---|---|---|---|---|---|---|
| `enqueue` | pr | P-01 | 8 | 8 | 1 | – | 256 | – | 3 s / 15 s |
| `enqueue-hot` | pr | P-01 | 8 | 1 | 1 | – | 256 | – | 3 s / 15 s |
| `enqueue-batch` | pr | P-02 | 4 | 4 | 100 | – | 256 | – | 3 s / 15 s |
| `dequeue-ack` | pr | P-03 | 8 | 8 | – | 1 | 256 | 4000 | 3 s / 15 s |
| `enqueue-ramp` | extreme | P-01 | 1,2,4,8,16,32,64,128,256 | = concurrency | 1 | – | 256 | – | 5 s / 30 s per step |
| `enqueue-hot-ramp` | extreme | P-01 | 1,2,4,8,16,32,64 | 1 | 1 | – | 256 | – | 5 s / 30 s per step |
| `enqueue-batch-ramp` | extreme | P-02 | 1,2,4,8,16,32,64 | = concurrency | 100 | – | 256 | – | 5 s / 30 s per step |
| `dequeue-ack-ramp` | extreme | P-03 | 1,2,4,8,16,32,64,128,256 | = concurrency | – | 1 | 256 | 12000 | 5 s / 30 s per step |

**Measured:**
- **Latency:** wall time of one operation (for P-03, the whole dequeue + acks cycle) on a monotonic
  clock, in ms. Percentiles use nearest rank.
- **Throughput:** operations (and messages) that completed inside the recorded window, divided by the
  window's length.
- **Errors:** any exception from an operation is counted and the worker carries on, with no retry.
  A run with errors fails.

### `P-04` session drain (`SessionQueueConsumer`)

Publish `sessions` × `messages` (each session's messages go in one `enqueue` call, unless rate
limited), then drain them with one `SessionQueueConsumer` (`maxConcurrentSessions` slots). Its
handler sleeps `settleMs` per message. Payload `{"session": id, "seq": n, "publishedAt": <unix ms>}`,
session IDs `s00000…`. The full definitions of the metrics (wall clock, ideal, efficiency, peak window,
claim, in-session wait, drain wait, between streams, delivery latency) are in
[sdks/dotnet/perf/README.md](../dotnet/perf/README.md#what-is-measured). An SDK measures streams by
wrapping its own client the way `RecordingDaprMQClient` does. The run fails if a message is missing
or a session's messages are handled out of order.

| Profile | Scale | Sessions × msgs | Settle | Slots | Other |
|---|---|---|---|---|---|
| `steady-drain` | pr | 200 × 20 | 100 ms | 20 | idle timeout 1 s |
| `session-churn` | pr | 300 × 2 | 50 ms | 20 | idle timeout 1 s |
| `deep-session` | pr | 4 × 1000 | 10 ms | 4 | idle timeout 1 s |
| `live-publish` | pr | 20 × 50 | 100 ms | 20 | idle timeout 1 s, concurrent publish, 200 ms ± 100 ms |
| `sdk-defaults` | pr | 40 × 5 | 1 s | 20 | – |
| `full` | extreme | 1000 × 100 | 1 s | 20 | – |
| `wide-drain` | extreme | 2000 × 10 | 50 ms | 200 | idle timeout 1 s |

The consumer uses prefetch 10, lease 30 s and idle timeout = lease unless the profile says otherwise.

### `P-05` queue drain (`QueueConsumer`)

Publish `messages` to a plain queue, then drain them with one `QueueConsumer` at its defaults
(`maxActiveMessages` 100, unlimited handlers, competing consumers) unless the profile says otherwise.
Its handler sleeps `settleMs` per message, or `tailMs` for every `tailEvery`-th one. Payload
`{"seq": n, "publishedAt": <unix ms>}`. The backlog is seeded first in `enqueue` batches of 100
(16 in parallel, or one at a time for `strictOrder`, so queue order is seq order). A profile with a
publish interval instead publishes one message at a time from one publisher, starting with the
consumer, so the wall clock includes publishing.

This is the plain-queue counterpart of `P-04`, and the stream-based counterpart of `P-03`'s
`dequeueLocked` loop: the profiles mirror the handler times measured when the `Consume` stream was
introduced ([proposals/continuous-receive.md](../../proposals/continuous-receive.md#results)).

| Profile | Scale | Messages | Settle | Consumer | Other |
|---|---|---|---|---|---|
| `queue-drain-instant` | pr | 4000 | 0 ms | defaults | – |
| `queue-drain` | pr | 4000 | 10 ms | defaults | – |
| `queue-drain-slow` | pr | 3000 | 100 ms | defaults | – |
| `queue-strict-order` | pr | 300 | 0 ms | `strictOrder` | – |
| `queue-live-publish` | pr | 50 | 10 ms | defaults | publish one every 200 ms ± 100 ms |
| `queue-drain-large` | extreme | 50000 | 10 ms | `maxActiveMessages` 500 | – |
| `queue-drain-tail` | extreme | 2000 | 100 ms | defaults | every 20th message takes 5 s |

**Measured:**
- **Throughput:** messages handled ÷ wall clock (consume start to the last message's handler finishing).
- **Ideal and efficiency:** ideal is the handler work spread over the handlers that can run at once
  (1 under `strictOrder`, else `maxConcurrentHandlers` capped by `maxActiveMessages`), but no less
  than the slowest single handler, nor than the last live-published message plus its handler.
  Efficiency is ideal ÷ wall clock. Both are null with an instant handler, where the run measures
  overhead alone.
- **Delivery latency:** publish to handler start. On `queue-live-publish` this is how fast a new
  message reaches a consumer that has been idle, so it shows the server's idle backoff.
- **Peak concurrent handlers**, time to the first message, and per-second throughput and busy-handler
  timelines.
- **Checks:** the run fails if a message is never handled, or, under `strictOrder`, if a message
  starts before an earlier one.

## Result format

Defined by [perf/result.schema.json](perf/result.schema.json) (schema version 2). An SDK writes:

- `<out>/sdk-<sdk>/runs/<runId>.json`: one run in full, including its per-second `timeline`
- `<out>/sdk-<sdk>/history.jsonl`: the same record without `timeline`, one line per run

`runId` is `<yyyyMMdd'T'HHmmss'Z'>_<sdk>_<envLabel>_<profile>`. Runs are grouped for trends and
regression checks by `(environment.label, topology.apiReplicas, scenario.key)`. The report puts
different `sdk` values on the same chart.

Every profile's scenario ID, name, scale and `key` are also listed in
[perf/profiles.json](perf/profiles.json). Each harness's unit tests (the .NET one included) check
their profiles against it, so a key that drifts in one language fails that SDK's tests.

The report and regression check come from one tool,
[perf/DaprMQ.PerfReport](perf/DaprMQ.PerfReport/), whatever language produced the results:

```bash
dotnet run --project sdks/testing/perf/DaprMQ.PerfReport -- report --results perf-results            # -> perf-results/report.html
dotnet run --project sdks/testing/perf/DaprMQ.PerfReport -- check  --results perf-results --sdk python --runs <runId>,<runId> [--gate]
```

## Running a harness

Every harness starts its own throwaway stack (build the API image first with
`./build-and-test.sh --skip-tests`), writes to `<repo>/perf-results/sdk-<sdk>/` by default, and
then calls the .NET report tool for `report.html` and the regression table. They take the same flags:
`--suite pr|extreme`, `--profile NAME`, `--api-replicas N`, `--env-label NAME`, `--out DIR`,
`--http URL --grpc URL` (an existing server instead of Testcontainers), `--gate` and `--baseline-branch B`.

| SDK | Run (from the SDK's directory) | Harness | Unit tests |
|---|---|---|---|
| .NET | `dotnet run -c Release --project perf/DaprMQ.Client.Perf -- --suite pr` | [perf/DaprMQ.Client.Perf](../dotnet/perf/DaprMQ.Client.Perf/) | `dotnet test tests/DaprMQ.Client.Perf.Tests` |
| Python | `python -m perf --suite pr` | [perf/](../python/perf/) | `pytest perf/tests` |
| TypeScript | `npm run perf -- --suite pr` | [perf/](../typescript/perf/) | `npx vitest run perf` (also part of `npm test`) |
| Java | `mvn -q -Pperf test-compile exec:exec -Dperf.args="--suite pr"` | [src/test/java/.../perf](../java/src/test/java/com/daprmq/client/perf/) | `mvn test` |

Each SDK's integration fixture builds the perf topology behind an option, so integration runs keep
the single-instance stack: `DaprTopology.perf(n)` in
[tests/integration/stack.py](../python/tests/integration/stack.py),
[tests/integration/daprmqServer.ts](../typescript/tests/integration/daprmqServer.ts) and
[DaprTopology.java](../java/src/test/java/com/daprmq/client/integration/DaprTopology.java).

The Java session drain times streams with `RecordingSessionClient`, which sits in package
`com.daprmq.client` (test sources) because `SessionStream` is only built through a package-private
stream factory.

## Coverage matrix

Legend: ✅ implemented and passing · ⬜ not yet

| Profile | .NET | Python | TypeScript | Java |
|---|:-:|:-:|:-:|:-:|
| **pr** | | | | |
| `enqueue` | ✅ | ✅ | ✅ | ✅ |
| `enqueue-hot` | ✅ | ✅ | ✅ | ✅ |
| `enqueue-batch` | ✅ | ✅ | ✅ | ✅ |
| `dequeue-ack` | ✅ | ✅ | ✅ | ✅ |
| `steady-drain` | ✅ | ✅ | ✅ | ✅ |
| `session-churn` | ✅ | ✅ | ✅ | ✅ |
| `deep-session` | ✅ | ✅ | ✅ | ✅ |
| `live-publish` | ✅ | ✅ | ✅ | ✅ |
| `sdk-defaults` | ✅ | ✅ | ✅ | ✅ |
| `queue-drain-instant` | ✅ | ✅ | ✅ | ✅ |
| `queue-drain` | ✅ | ✅ | ✅ | ✅ |
| `queue-drain-slow` | ✅ | ✅ | ✅ | ✅ |
| `queue-strict-order` | ✅ | ✅ | ✅ | ✅ |
| `queue-live-publish` | ✅ | ✅ | ✅ | ✅ |
| **extreme** | | | | |
| `enqueue-ramp` | ⬜ | ⬜ | ⬜ | ⬜ |
| `enqueue-hot-ramp` | ⬜ | ⬜ | ⬜ | ⬜ |
| `enqueue-batch-ramp` | ⬜ | ⬜ | ⬜ | ⬜ |
| `dequeue-ack-ramp` | ⬜ | ⬜ | ⬜ | ⬜ |
| `full` | ✅ | ⬜ | ⬜ | ⬜ |
| `wide-drain` | ⬜ | ⬜ | ⬜ | ⬜ |
| `queue-drain-large` | ⬜ | ⬜ | ⬜ | ⬜ |
| `queue-drain-tail` | ⬜ | ⬜ | ⬜ | ⬜ |

## Remaining work

Status as of 2026-10-09. Phase 1 is done: this spec, the result schema, the report tool, the .NET perf
topology and harness, both workflows, and the old `server/tests/DaprMQ.PerformanceTests` deleted.
Phase 2's Python, TypeScript and Java harnesses are built. The items below are still open. Tick or
delete them as they land.

### Phase 1 follow-ups (.NET)

- [ ] **Run the extreme profiles end to end** (`./run-perf-test.sh --suite extreme`, or one at a
  time with `--profile enqueue-ramp` etc.), then tick them above. Look out for:
  - `dequeue-ack-ramp` seeding 256 queues × 12000 items (~3M messages). Check the seed time is
    tolerable and that no step drains early, which fails the run.
  - `wide-drain` (200 slots).
  - The default 3 replicas plus nginx running all at once on one runner.
- [ ] **First real CI runs.** Neither [perf.yml](../../.github/workflows/perf.yml) nor
  [perf-extreme.yml](../../.github/workflows/perf-extreme.yml) has run on GitHub yet. The first run
  converts the `perf-results` branch's schema-1 .NET history in place
  ([checkout-perf-results](actions/checkout-perf-results/action.yml), `--migrate-v1`) and pushes the
  converted files. A dry run on a copy of the branch converted 35 runs cleanly. Once the branch has been
  converted, delete the "Convert schema-1 history" step and, later, `RunRecords.MigrateV1*` and `--migrate-v1`.
- [ ] **Turn on `--gate`** in perf.yml once a couple of weeks of `main` history show each metric's noise.
  The load-profile tolerances in
  [RegressionCheck.cs](perf/DaprMQ.PerfReport/RegressionCheck.cs) (20% msg/s, 50% p95, 100% p99) are
  first guesses.
- [ ] **Single-process load generator.** On a 4-vCPU runner a single process may run out of CPU before
  3 replicas do, especially in Python. If ramps flatten because of the client, add a
  `--workers N` (processes) option to the spec and the harnesses.

### Phase 2: Python, TypeScript and Java harnesses

Built 2026-10-09: all three harnesses, their fixtures' perf topology, the shared
[perf/profiles.json](perf/profiles.json), and their jobs in both workflows (see
[Running a harness](#running-a-harness)). Every pr profile passes locally in all three. The Python
and Java SDKs gained `session_idle_timeout_seconds`/`sessionIdleTimeoutSeconds` on the session
consumer and stream along the way, which the P-04 profiles need.

- [ ] **Run their extreme profiles** ([perf-extreme.yml](../../.github/workflows/perf-extreme.yml)), then tick them.
- [ ] **Go.** The Go SDK arrived after this plan. Give it a harness the same way (its fixture is
  [sdks/go/integration](../go/integration/)), add `go` to the schema's `sdk.name` enum, and add a column above.

### Phase 3: extreme extras

- [ ] **Soak profile** (a new `P-05`): mixed producers and a consumer at a fixed rate for 30–60 min,
  with timelines of throughput, latency, error rate, and ideally server memory, to catch leaks and
  slow degradation. Define its parameters here first, then implement it in every SDK.
- [ ] **Scheduler-heavy load.** The scheduler isn't on DaprMQ's hot paths today, so HA mostly matters
  for production parity. If a reminder- or job-driven feature (e.g. topic relay) becomes performance-relevant,
  add a profile for it, and include `schedulerReplicas` in the series key then.
- [ ] **Dapr 1.19:** placement folds into the scheduler. When the images move to 1.19, drop the
  placement container and `--placement-host-address` from every fixture.

### Open decisions

- **The report tool is .NET.** Every SDK's CI job installs .NET for `check`, and the publish job for
  `report`/`merge`. It only reads JSON, so it could be ported if the cross-language dependency becomes a
  problem.
- **Ramp headline = best step.** A ramp's `metrics` (and so its regression check) uses its best step.
  The knee concurrency might be a better number to track.
