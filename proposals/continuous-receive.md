# Continuous receive for the DaprMQ Dapr pub/sub components

Status: implemented (server `Consume` RPC and `ExtendLockBatch`, Go SDK `Client.Consume`, and
`receiveMode: stream` as the components' default). See [Results](#results).

## Context

The Dapr pub/sub components `pubsub.daprmq.topics` and `pubsub.daprmq.queues` live in the
components-contrib fork (shared code in `common/component/daprmq`). Both consume a DaprMQ queue by
polling `DequeueLocked` over REST, calling the app's handler, then acknowledging or nacking with a
further REST call each. A renewal loop in the component extends every held lock until it is settled.

**Today:** poll up to `dequeueCount` (10), run up to `maxConcurrentHandlers` (10), wait for every
handler in the batch, then poll again. An empty queue sleeps `pollInterval` (1s). A single slow
message stalls the other nine slots, and throughput tops out at about 10 divided by the slowest
handler's time.

A comparison with the Azure Service Bus components found that the difference is not the transport
but the receive model. Service Bus receives over a long-lived AMQP link with credit-based flow
control: the client grants credit (`maxActiveMessages`, default 1,000), the broker pushes messages up
to that credit, and settling a message on the same link frees credit for the next one. There is no
"ask for messages" request.

DaprMQ already has that model, but only for sessions. `ConsumeSession` is a bidirectional gRPC
stream: the client opens it with a prefetch window, the server streams deliveries, the client sends
Ack, Nack and DeadLetter frames back on the stream, and since
[ADR 0001](../docs/ADR/0001-consume-session-ack-driven-prefetch-refill.md) the server refills the
window when acks bring it down to half. Moving the component's polling to the existing unary gRPC
`DequeueLocked` would save JSON encoding but keep every round trip, so it is not proposed.

This proposal adds the same stream for plain queues and moves the components onto it.

## Proposal

### 1. A `Consume` stream for plain queues (server)

A new RPC beside `ConsumeSession`, sharing its loop:

```proto
rpc Consume(stream ConsumeRequest) returns (stream ConsumeResponse);

message ConsumeRequest {
  oneof payload {
    ConsumeStart start = 1;           // must be first
    ConsumeAck ack = 2;               // { lock_id }
    ConsumeNack nack = 3;             // { lock_id }
    ConsumeDeadLetter dead_letter = 4; // { lock_id }
  }
}

message ConsumeStart {
  string queue_id = 1;
  int32 prefetch_count = 2;            // in-flight window, default 1
  int32 lock_ttl_seconds = 3;          // per-lock TTL, default 30
  bool allow_competing_consumers = 4;  // as on DequeueLocked
}

message ConsumeResponse {
  oneof payload {
    Delivered delivered = 1;           // { lock_id, item_json, priority, lock_expires_at, delivery_count }
    SettleFailed settle_failed = 2;    // { lock_id, error_code, message } - not terminal
    ConsumeError error = 3;            // terminal
  }
}
```

Server behaviour:

- **Window.** Dequeue up to `prefetch_count - outstanding` with `DequeueLocked`, passing
  `allow_competing_consumers` so several streams can hold locks on one queue. Refill when settles
  bring `outstanding` down to half the window, as ADR 0001 does for sessions. An empty queue waits the
  same internal `PollInterval` (200ms).
- **Lock renewal moves to the server.** A session's locks live for its lease; a plain queue's locks
  each have a TTL. The stream renews the locks it has outstanding at about `lock_ttl_seconds / 2`
  for as long as the stream is open, so clients never send ExtendLock. Renewing hundreds of locks a
  tick needs a batch call on the queue actor (one actor turn, one state save), mirroring
  `AcknowledgeBatch`: `ExtendLockBatch` renews each lock to expire `ttl` seconds from now, never
  earlier than it already does. Unlike ExtendLock it adds nothing to the current expiry, because
  in testing a renewal that added one interval per tick fell behind whenever a tick ran late,
  until locks expired under a running handler.
- **Disconnect returns work.** When the stream ends, the server nacks every outstanding lock, so the
  items go back to their positions straight away instead of after the lock TTL. If the gateway itself
  dies, the locks lapse after their TTL as they do today.
- **Settle failures are reported per lock.** `ConsumeSession` reports a rejected Ack with its
  terminal `SessionError` frame while keeping the stream open. `Consume` uses a separate,
  non-terminal `SettleFailed` frame that names the lock.
- **Delivery count.** `Delivered` carries the item's delivery count, so the component can pass it to
  the app as `metadata.DeliveryCount`. Today only the Nack result returns a delivery count, so the
  queue actor's dequeue result has to carry it too.

### 2. Go SDK

`Client.Consume(ctx, queueID, *ConsumeOptions) (*QueueStream, error)`, modelled on the existing
`SessionStream`: `Receive` returns the next delivery, and `Ack`, `Nack` and `DeadLetter` send frames.
`Close` half-closes the stream and waits for the server to finish, matching the consumer-stop
behaviour the SDKs already share.

### 3. The components use the stream

Each subscription opens one `Consume` stream on its queue: the topic subscriber's queue for
`daprmq.topics`, the topic's own queue for `daprmq.queues`. Publishing stays on REST, because topics
have no gRPC API.

- The poll loop, `pollInterval` and the component's lock renewal loop go away.
- Deliveries go to a handler pool capped by `maxConcurrentHandlers`. Deliveries over that cap wait
  locked, and the server keeps their locks alive.
- Settling is a frame on the stream, not a REST call.
- Unsubscribe or close: stop handing out deliveries, wait for running handlers to settle, then close
  the stream. The server nacks whatever was delivered but not started.

## Settings

| Setting | Today | Proposed | Service Bus equivalent |
| --- | --- | --- | --- |
| `maxActiveMessages` | n/a (= batch size) | 100, the stream's `prefetch_count` | `maxActiveMessages`, 1,000 |
| `maxConcurrentHandlers` | 10 | 0 = unlimited, bounded by `maxActiveMessages` | `maxConcurrentHandlers`, unlimited |
| `lockTTL` | 30s, renewed by the component | 30s, renewed by the server | `lockDurationInSec`, server default 60s |
| `dequeueCount` | 10, the whole cycle | removed | 1 per receive call |
| `pollInterval` | 1s | removed (server polls at 200ms) | none |
| `lockRenewalInterval` | 10s | removed (server renews at TTL / 2) | `lockRenewalInSec`, 20s |

The proposed `maxActiveMessages` default is 100 rather than 1,000 because every held message is a
lock the server has to renew and a message that waits behind slower ones.

## Constraints and open questions

- **Strict-order mode.** With `allowCompetingConsumers: false` or `concurrencyMode: single`, the
  component opens the stream with a window of 1. A wider window lets later items be delivered before
  a nacked one comes back, as the `ConsumeSession` proto already notes.
- **Retry pacing.** A stream redelivers a nacked message even faster than polling. Land a
  `maxRetriableErrorsPerSec` limiter in the component (Service Bus's default is 10) before or with
  this change, taking a token before a failed message is nacked.
- **Gateway and actor load.** Every open stream is a loop in a gateway pod that polls its queue actor
  every 200ms while the queue is empty. Many replicas on one queue mean many loops on one actor. This
  needs measuring before raising defaults.
- **Idle latency.** The 200ms server poll beats today's 1s `pollInterval`, but only a notify-on-enqueue
  from the queue actor makes delivery immediate. ADR 0001 lists that as not yet addressed, and REST
  long-poll would need the same thing.
- **Fairness across instances.** A large window lets one replica lock most of a short queue while
  others sit idle. The default of 100 limits this, and it can be lowered per component.
- **gRPC as a requirement.** The components already require `grpcEndpoint`. A deployment that can
  only reach the REST API needs a fallback (below).

## Fallback: continuous receive over REST

If the stream is not available, the component can still refill as handlers finish:

1. A `maxActiveMessages` semaphore counts messages locked by this instance, handled or not.
2. The poller waits for at least one free slot, then calls `DequeueLocked` for
   `min(freeSlots, dequeueCount)` items. It sleeps `pollInterval` only when a dequeue returns nothing.
3. A slot is released when its message is settled, not when the batch ends.

This keeps client-side lock renewal, so renewal capacity becomes the limit: with hundreds of held
locks, a tick of up to 20 concurrent ExtendLock calls every 10s has to finish well inside the 30s TTL.
It needs the same batch extend, or renewal concurrency scaled with `maxActiveMessages`, plus a warning
when a tick runs past `lockRenewalInterval`.

## Rollout and validation

1. **Server:** `Consume` with tests that follow the `ConsumeSession` suite. Cover window refill,
   server-side renewal keeping a slow message locked past its TTL, nack-on-disconnect,
   `SettleFailed`, and competing streams on one queue. Add the batch extend to the queue actor.
2. **Go SDK:** `Client.Consume` and `QueueStream`, with unit tests against a fake stream and an
   integration test against the server.
3. **Components:** switch both to the stream, keeping the REST poll loop behind a setting until the
   benchmarks are in. `maxActiveMessages: 10` with `maxConcurrentHandlers: 10` should reproduce
   today's throughput. Pub/sub conformance must pass for both types.
4. **Benchmark** today's loop, the REST fallback and the stream on one queue with fixed-latency
   handlers (10ms, 100ms, and 1s with a 5% tail at 10s). Compare messages per second, p99 end-to-end
   latency, gateway CPU and actor calls per message. Check strict-order mode for in-order handling
   under nacks.

## Related work

- [ADR 0001](../docs/ADR/0001-consume-session-ack-driven-prefetch-refill.md): the refill-on-settle
  rule this stream reuses.
- Retry pacing (`maxRetriableErrorsPerSec`), above: a prerequisite.
- Notify-on-enqueue from the queue actor: removes the remaining idle poll for both `Consume` and
  `ConsumeSession`.

## Results

Measured 2026-10-09 on a laptop against the docker-compose stack (one gateway, three workers,
Postgres), through a locally built daprd with `pubsub.daprmq.queues`: one subscriber, N messages
bulk-published to one queue, each handler sleeping a fixed time. Time is first publish to last
message handled. Poll mode used its defaults (`dequeueCount` 10); stream mode used its defaults
(`maxActiveMessages` 100).

| Messages × handler time | Poll | Stream | Speed-up |
| --- | --- | --- | --- |
| 1000 × 0ms | 4.3s | 1.5s | 2.9× |
| 1000 × 10ms | 5.9s | 1.1s | 5.4× |
| 1000 × 100ms | 15.5s | 2.1s | 7.4× |
| 200 × 1s | 21.8s | 2.5s | 8.7× |

Poll mode never ran more than 10 handlers at once; the stream ran up to 100.

What it took to get there:

- **Settles are batched.** The first version applied each Ack frame with its own `Acknowledge`
  actor call before reading the next frame, which capped a stream at one call's round trip
  (about 6ms, so roughly 150 messages/s) whatever the handler time, and made the stream slower than
  polling for fast handlers. Acks that queue up while one call is in flight now go to the actor
  together in one `AcknowledgeBatch`.
- **Renewal resets rather than adds.** Adding one interval per tick drifted whenever a tick ran
  late, and truncating "now" to whole seconds cost up to a second more; with short TTLs, locks
  expired under running handlers. `ExtendLockBatch` sets each lock to `now + ttl` from sub-second
  time, and the stream renews every third of the TTL.
- **The test app's listen backlog.** Early runs showed messages arriving in clumps of five every
  two seconds. That was the Python test subscriber's default listen backlog of 5 refusing daprd's
  connection bursts, which Dapr reports as retriable errors; the component then (correctly) nacked
  and paced them. Not a DaprMQ issue, but a reminder that the app's own concurrency limits show up
  as redeliveries.

Raw REST calls against the same stack, for reference: `DequeueLocked` of 200 items in 59ms;
`Acknowledge` about 6ms one at a time and about 430/s with 50 concurrent callers on one queue;
`AcknowledgeBatch` of 200 locks in 117ms. Not yet investigated: what bounds the stream at about
700-900 messages/s with an instant handler.

