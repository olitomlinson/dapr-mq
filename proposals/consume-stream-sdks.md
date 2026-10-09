# Consume stream in every SDK, and a managed QueueConsumer

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
