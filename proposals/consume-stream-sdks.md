# Consume stream in every SDK, and a managed QueueConsumer

Status: implemented (server idle backoff, `consume` in .NET, Python, TypeScript and Java, `QueueConsumer` in all five
SDKs, QS and QC rows ticked for every SDK, docs, the .NET autoscale worker). See [Results](#results).

## Context

The `Consume` gRPC stream for plain queues landed with
[continuous-receive.md](continuous-receive.md) (see its Results). The server keeps up to
`prefetch_count` locked messages delivered and refills as they are settled. It renews their locks
and batches their acks. When the stream closes, it returns unsettled messages straight away. Through
the Dapr pub/sub component it handled messages 3 to 9 times faster than polling `DequeueLocked`.

Today only the Go SDK exposes it (`Client.Consume` returning a `QueueStream`;
[sdks/go/queue_stream.go](../sdks/go/queue_stream.go)). Every SDK has a managed
`SessionQueueConsumer` for session queues, built on `ConsumeSession`, but none has one for plain
queues. Plain-queue consumers are loops users write around `DequeueLocked`, for example
[examples/python/consumer/scenarios.py](../examples/python/consumer/scenarios.py) and
`examples/typescript/consumer/src/scenarios.ts`.

**Decision: the stream becomes the recommended way to run a long-lived consumer, and the REST
calls stay.** `DequeueLocked`, `Acknowledge`, `AcknowledgeBatch`, `ExtendLock`, `Nack` and
`DeadLetter` remain the right tool for:

- run-to-completion work: KEDA ScaledJobs, cron jobs and serverless functions that take a batch and
  exit. A stream's prefetch would lock messages the job never handles.
- admin and inspection tools, and one-off operations.
- clients without HTTP/2 or gRPC: browsers (the dashboard), some proxies, `curl`.
- callers that want explicit control of individual locks.

No SDK method is removed or deprecated.

## Goals

1. `Consume` and a stream type in the .NET, Python, TypeScript and Java SDKs, matching Go.
2. A managed, handler-based `QueueConsumer` in all five SDKs that reconnects, modelled on each SDK's
   `SessionQueueConsumer`.
3. Long-running example consumers move to it; batch and ScaledJob guidance stays on `DequeueLocked`.
4. The cost of idle streams is measured before large replica counts are recommended.

## Work

### Step 0: measure idle stream cost (before steps 2-3 ship)

Each open stream is a loop in a gateway pod that calls its queue actor every 200ms
(`DaprMQGrpcService.PollInterval`) while the queue is empty. With N idle consumers that's about 5N
actor calls a second.

- Against the docker-compose stack, open 10, 50 and 200 idle `Consume` streams on one queue and
  on separate queues. Record gateway and worker CPU (`docker stats`), and the latency of a
  `DequeueLocked` made alongside them.
- Also restart the gateway with 50 streams open, each holding messages. Confirm every unsettled
  message is redelivered once, and record the redelivery burst.
- If idle cost is material: back the empty-queue wait off (200ms doubling to a cap of about 2s,
  reset on a delivery). That costs idle-to-first-message latency, so record both.
  Notify-on-enqueue from the queue actor is the full fix and stays a separate change.

Record results under a Results section in this file.

### Step 1: `Consume` in .NET, Python, TypeScript and Java

Mirror the Go API (`Client.Consume`, `QueueStream`, `QueueDelivery`, `ConsumeOptions`;
[sdks/go/queue_stream.go](../sdks/go/queue_stream.go), [sdks/go/types.go](../sdks/go/types.go)) in
each language's idiom, next to its existing `ConsumeSession`.

- **Options:**
  - `prefetchCount` (default 1, max 1000)
  - `lockTtl` (default 30s, sent as whole seconds rounded up)
  - `allowCompetingConsumers` (default false)
  - an `onSettleFailed(lockId, error)` callback
- **Delivery:** `lockId`, `item` (raw JSON), `priority`, `lockExpiresAt` and `deliveryCount` (1 on a
  first delivery), with `ack()`, `nack()` and `deadLetter()`.
- **Stream:**
  - Receive or iterate deliveries.
  - A `SettleFailed` frame goes to `onSettleFailed` and the stream carries on.
  - An `Error` frame ends it with the SDK's typed error.
  - `close()` half-closes and waits up to 5s for the server to end the stream, then cancels, as
    each SDK's session stream does.
  - Settling after close raises the SDK's "stream closed" error.

Where each SDK keeps its session stream, and how it gets the proto:

| SDK | Session stream lives in | Proto stubs |
| --- | --- | --- |
| .NET | `sdks/dotnet/src/DaprMQ.Client/DaprMQClient.cs`, `IDaprMQClient.cs`, `Models.cs` | Built from the server proto by `Grpc.Tools` (`<Protobuf Include=…>` in the csproj); nothing to regenerate |
| Python | `sdks/python/src/daprmq_client/client.py`, `types.py` | Checked in under `daprmq_client/grpc/`; regenerate `daprmq_pb2.py`, `.pyi` and `_grpc.py` with `grpcio-tools` (current files are protobuf Python 7.35.1) |
| TypeScript | `sdks/typescript/src/client.ts`, `types.ts`, `grpc/daprmqGrpcClient.ts` | Loaded at runtime from the server proto by `@grpc/proto-loader`; add the `Consume` method to the client wrapper |
| Java | `sdks/java/src/main/java/com/daprmq/client/SessionStream.java`, `DaprMQClient.java`, `ConsumeSessionOptions.java` | Generated at build time from the server proto (`daprmq.protoSourceRoot` in `pom.xml`) |

**Tests:**
- Unit tests against each SDK's existing fake for `ConsumeSession`.
- Integration tests for QS-01 to QS-04 in [sdks/testing/INTEGRATION_TESTS.md](../sdks/testing/INTEGRATION_TESTS.md); tick each SDK's column as it passes.
- The Go tests in `sdks/go/integration/queue_stream_test.go` are the reference.

### Step 2: managed `QueueConsumer` in all five SDKs

A handler-based consume loop over `Consume`, shaped like each SDK's `SessionQueueConsumer` (Go:
[sdks/go/session_queue_consumer.go](../sdks/go/session_queue_consumer.go)).

Options:

| Option | Default | Meaning |
| --- | --- | --- |
| `maxActiveMessages` | 100 | The stream's `prefetchCount` |
| `maxConcurrentHandlers` | 0 = unlimited, bounded by `maxActiveMessages` | Handlers running at once |
| `lockTtl` | 30s | Passed to the stream; the server renews |
| `allowCompetingConsumers` | true | Replicas share the queue |
| `strictOrder` | false | Forces `prefetchCount` 1, one handler and `allowCompetingConsumers` false |
| `onHandlerError` | `Nack` | `Nack` (redeliver) or `DeadLetter` |
| `maxRetriableErrorsPerSec` | 10 | Paces nacks after handler errors, as the Dapr component does |
| `minBackoff` / `maxBackoff` | 1s / 60s | Reconnect backoff after the stream breaks; resets after a delivery |
| `drainTimeout` | 30s | How long `stop` waits for running handlers |

Behaviour, matching the Dapr component's stream mode (`serveStream` in components-contrib
`common/component/daprmq/stream.go`):

- **Settling:** each delivery goes to the handler. Success acks it; an error applies
  `onHandlerError`, taking a pacing token first when it nacks.
- **Breaks:** if the stream breaks while the consumer is running, it reopens with backoff. The server
  has already returned whatever was unsettled.
- **`stop()`:**
  1. Stops handing out deliveries.
  2. Lets running handlers finish and settle, up to `drainTimeout`, then cancels them.
  3. Closes the stream, so the server returns everything not yet handled.

  This is the same half-close-not-cancel rule the session consumers follow (K-09).

New matrix rows in [INTEGRATION_TESTS.md](../sdks/testing/INTEGRATION_TESTS.md):

| ID | Capability |
| --- | --- |
| QC-01 | Handler success acks; the queue ends empty |
| QC-02 | Handler error with `Nack` redelivers with `deliveryCount` 2; with `DeadLetter` it lands in `{queueId}-deadletter` |
| QC-03 | `maxConcurrentHandlers` is never exceeded |
| QC-04 | `strictOrder` handles messages in queue order, including after a nack |
| QC-05 | `stop` drains running handlers, which ack; messages not started are back in the queue straight away |
| QC-06 | A stream broken mid-run (restart the gateway container) reconnects, and every message is handled at least once |

### Step 3: examples and docs

- Move the long-running example consumers (`examples/*/consumer`) to `QueueConsumer`, keeping any
  scenario that exists to demonstrate `DequeueLocked`.
- Leave the KEDA ScaledJob example on `DequeueLocked` and say why in its README.
- [docs/CLIENT_SDK.md](../docs/CLIENT_SDK.md): add `consume` and `QueueConsumer` to the shared
  shape, with a short "which to use" note covering the cases in Context.
- Each SDK's own doc: add a Consume section, as
  [sdks/go/docs/CLIENT_SDK.md](../sdks/go/docs/CLIENT_SDK.md) has.

## Order and checks

Step 0 first, because its result can change the defaults in step 2. Step 1 can then proceed per SDK
in parallel. Step 2 follows step 1 in each SDK, and step 3 comes last.

For each SDK:
- its unit tests pass
- its integration tests pass (`./build-and-test.sh` builds the image they use)
- the QS and QC rows are ticked

Before committing, run `./build-and-test.sh` per CLAUDE.md.

## Open questions

- `allowCompetingConsumers` defaults to false on `Consume`, matching `DequeueLocked`, but true on
  `QueueConsumer`, because replicas usually share a queue. Keep that split, or make both true?
- Should `QueueConsumer` pass the delivery count to handlers in a context object, as the session
  consumers pass `SessionMessageContext`? Recommended: yes, with `lockId` and `deliveryCount`.

## Results

Measured 2026-10-09 on a laptop against the docker-compose stack (one gateway, three workers, Postgres).

### Step 0: idle stream cost

N idle `Consume` streams (`prefetchCount` 10, competing consumers), on N queues or all on one. CPU is the mean of five
`docker stats` samples (100% = one core), taken while a probe enqueued, `DequeueLocked`-ed and acked one item at a time
on another queue.

| Idle streams | Queues | Gateway + sidecar CPU | Workers + sidecars CPU | Postgres CPU | Probe `DequeueLocked` p50 / p99 |
| --- | --- | --- | --- | --- | --- |
| 0 | - | 8.5% | 17.6% | 3.7% | 5.3 / 14.6 ms |
| 10 | separate | 11.4% | 25.7% | 5.7% | 5.1 / 12.5 ms |
| 50 | separate | 18.9% | 42.5% | 6.5% | 5.0 / 11.6 ms |
| 200 | separate | 45.3% | 97.5% | 13.3% | 5.0 / 13.5 ms |
| 10 | one | 12.1% | 22.8% | 4.3% | 4.8 / 11.6 ms |
| 50 | one | 25.5% | 44.7% | 5.5% | 4.8 / 13.1 ms |
| 200 | one | 78.1% | 125.2% | 12.6% | 4.9 / 13.5 ms |

Cost grew linearly: about 0.2% of a core in the gateway and 0.4-0.5% in the workers per idle stream, so 200 idle
consumers cost over a core. Other callers' latency didn't change. That is material, so the empty-queue wait now backs
off: 200 ms doubling to 2 s (`DaprMQGrpcService.MaxIdlePollInterval`), reset by a delivery, and never past the next
renewal of locks the stream already holds.

| With backoff | Gateway + sidecar CPU | Workers + sidecars CPU | Postgres CPU | Probe p50 / p99 |
| --- | --- | --- | --- | --- |
| 0 streams | 16.3% | 17.3% | 3.7% | 5.1 / 21.2 ms |
| 200 streams, separate queues | 14.4% | 29.9% | 7.2% | 5.4 / 13.6 ms |
| 200 streams, one queue | 16.4% | 31.5% | 4.6% | 5.0 / 11.8 ms |

200 idle streams now cost about an eighth of a core. The price is idle-to-first-message latency: on a stream idle for
8 s, an enqueued message arrived after 59 ms at p50 (274 ms max) before the backoff and about 1.05 s after it. A
stream that has backed off fully polls every 2 s, so a message waits up to 2 s, 1 s on average. A stream under load
never backs off. Notify-on-enqueue from the queue actor would remove both the polling and this latency.

### Step 0: gateway restart with streams open

50 streams each held 10 unsettled messages (`prefetchCount` 10, 30 s lock TTL), then `docker restart gateway-1`. The
gateway was ready again 3.3 s later and the streams reopened. Every one of the 500 messages was redelivered exactly
once (`DeliveryCount` 2, no duplicates), all within 112 ms of each other, but only 29.6 s after the restart began:
when their locks lapsed. The gateway's graceful shutdown didn't nack them. Its log shows no nack attempts, so the
Consume handlers didn't reach their cleanup before the process exited. A broken connection does return messages
straight away (QC-06 relies on that), so this affects only gateway shutdown, for example a rolling deploy.

### Also found

- **One HTTP/2 connection carries at most 100 concurrent streams.** That is Kestrel's default
  `MaxStreamsPerConnection`, and the server doesn't change it. A client that opens a 101st stream on one connection
  waits until another closes: grpc-go blocks in `Consume`. This applies to `SessionQueueConsumer` slots as well, and
  the SDK docs' advice to share one client for "tens or hundreds" of streams needs a caveat. The benchmark used one
  connection per 50 streams.

### Decisions

- `allowCompetingConsumers` stays split: false on `consume`, matching `DequeueLocked`; true on `QueueConsumer`, because
  replicas usually share a queue. `strictOrder` turns it off.
- `QueueConsumer` handlers get a context object with `lockId`, `item`, `priority` and `deliveryCount`.
- `QueueConsumer` options follow each SDK's `SessionQueueConsumer`: backoff in whole seconds in .NET, Python,
  TypeScript and Java, and as `time.Duration` in Go. In Go, zero means the default, so `MaxRetriableErrorsPerSec` 0 is
  10 and a negative value means unpaced, and `AllowCompetingConsumers` is a `*bool` (nil is true). Elsewhere, 0 means
  unpaced.
- Settling on a closed stream raises a new `StreamClosed` error in .NET, Python, TypeScript and Java, matching Go's
  `ErrStreamClosed`.
- QC-06 breaks the stream through a local TCP proxy rather than by restarting a container: Testcontainers re-maps a
  restarted container's host ports.
- The examples' scenario consumers all exist to demonstrate the REST calls, so they stay. The only long-running
  example consumer, the .NET KEDA autoscale worker, now runs a `QueueConsumer` with `MaxActiveMessages` set to
  `WORKER_BATCH_SIZE` and one handler, which keeps each replica's pace and the backlog KEDA sees as they were. It was
  smoke-tested against docker-compose (a stop mid-run acked the running message and returned the other 29 at once),
  and `k8s-deploy-and-test.sh --keda` passed on Docker Desktop: the worker scaled out from 0 to 2 replicas 54 s after
  the load, both replicas consumed, and it was back at 0 93 s after the load. The ScaledJob checks also passed.

### Follow-ons

- End open Consume streams when the gateway begins shutting down (link their token to `ApplicationStopping`), so a
  rolling deploy returns their messages straight away instead of after the lock TTL.
- Raise or document the 100-streams-per-connection limit.
- Notify-on-enqueue from the queue actor, to drop the idle poll and its up-to-2 s latency.
