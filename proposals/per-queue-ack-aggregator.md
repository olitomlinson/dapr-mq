# Per-queue ack aggregator in the gateway

**Status:** proposed. Follows the KEDA load tests of 2026-10-18 (`examples/keda/test/load.sh`) and
[docs/issues/lock-sweep-reads-are-unbounded.md](../docs/issues/lock-sweep-reads-are-unbounded.md).

## Context

A queue is one `QueueActor`, and the actor runs one turn at a time. Every settle, refill and renewal
for the queue queues behind the turn in progress. Each turn has a fixed cost on top of its real work:
a metadata read, the expiry sweep on a dequeue, and one state transaction. On the kind cluster used
for the load tests, a transaction averaged 57ms.

Each Consume stream settles its own acks
([DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs), the settler in
`Consume`):
- The reader puts each Ack frame on the stream's own channel.
- The stream's settler drains that channel into one `AcknowledgeBatch`.
- Acks that arrive while a batch is in flight go in the next one.

So batching only happens **within one stream**, and a batch can never be larger than the stream's
prefetch window: a stream can't have more unsettled items than that.

With many competing consumers on one queue, the actor therefore gets many small ack calls at once,
one per stream. In the load tests (100,000 messages, 81 ready workers, 1ms of work per item, 3
gateway pods):

| Workers × handlers, window | Drained in | Settle timeouts | Size of each timed-out batch |
|---|---|---|---|
| 81 × 1, window 5 | 733s | 0 | – |
| 81 × 1, window 17 | 660s | 637 | 17, a full window every time |
| 81 × 10, window 20 | 639s | 1,544 | 20, a full window every time |
| 10 × 10, window 100 (earlier run) | ~4 min | – | – |

- **Throughput didn't grow with consumers.** The queue actor topped out at about 136–156 messages
  a second with 81 streams. With 10 streams it was about 300–440 a second.
- **Each stream sends its own ack turn.** Each of those turns pays the fixed cost to settle at most
  one window.
- **Acks wait behind each other.** With up to 81 ack calls queued on the actor at once, each waits
  behind the others. Past the gateway's 10-second `SettleTimeout`, the whole batch fails and its
  locks are left to expire and redeliver.
- **Timeouts make it worse.** Each timed-out call is also a cancelled actor call. Before Dapr 1.19 that
  could start overlapping turns ([dapr/dapr#10835](https://github.com/dapr/dapr/issues/10835), fixed in the
  1.19.0 runtime); now it only wastes the call.

Batching is already at its maximum per stream. The batches are small because the window is small,
and the window should stay small: see "Prefetch sizing" below. The lever left is to combine acks
**across streams**.

## Proposal

Add a per-queue ack aggregator to the gateway. It's a singleton service shared by every Consume
stream the gateway pod serves. Acks for the same queue, from any stream, go to the actor together,
with **at most one `AcknowledgeBatch` in flight per queue per gateway pod**.

With 3 gateway pods, that's at most 3 concurrent ack calls into a queue actor, however many consumers
there are. Each call carries every ack that arrived, on any stream, while the previous one was in
flight.

### Behaviour

- **Submit.** A stream's settler gives the aggregator its acks for the queue and awaits the per-lock
  outcomes, as it awaits `AcknowledgeAsync` today. It then reports `SettleFailed` for any rejected
  lock and signals a refill, as now.
- **Flush.** The aggregator keeps one queue of pending acks per queue id and one flush loop per queue:
  - While acks are pending, take up to 1,000 (the actor's `AcknowledgeBatch` cap) and send one
    `AcknowledgeBatch`.
  - Hand each lock's outcome back to the stream that submitted it.
  - Repeat.

  There's no timer. Batches form from whatever arrived during the previous flush, which is the same
  opportunistic rule each stream uses today, applied to the whole queue.
- **Outcomes.** These are per lock, as now: `ACKNOWLEDGED`, `LOCK_NOT_FOUND`, `LOCK_EXPIRED` and
  `INVALID_LOCK_ID`. A whole-call failure (an error code, a timeout, or a transport failure) is
  reported to every stream in the batch as `SETTLE_FAILED`, with the reason from
  `DescribeSettleFailure`.
- **Timeout.** Each flush runs on its own `SettleTimeout` token, never on a stream's cancellation.
  Today's settle calls behave the same way: once an Ack frame is read, the client going away must not
  abandon it.
- **Nack and DeadLetter are unchanged.** They stay per call through `SettleAsync`. Nack restores to
  position, and DeadLetter calls another actor, so neither is a single-transaction batch. Both are rare
  next to acks.
- **Ordering** isn't affected. Acks aren't ordered today: the settler already applies a batch's acks
  before its nacks.
- **Lifecycle.** A queue's entry is created on its first ack, and removed once it has been idle for a
  while (say 60s) with nothing pending. Gateway shutdown drains any flush in progress.

### What stays the same

- **The protocol, the SDKs and the actor.** This is gateway-only, so a single gateway release rolls
  it out.
- **ConsumeSession.** Each session is its own actor with one stream, so there's no fan-in to merge.
- **Unary `Acknowledge` and `AcknowledgeBatch`** (REST and gRPC) are out of scope for v1. See
  "Later".

### Configuration

- `DAPRMQ_CONSUME_ACK_AGGREGATION` (default `true`) turns the aggregator on. `false` keeps today's
  per-stream settle, for an A/B comparison or as a fallback.
- There's no linger setting. If measurement shows small batches at low load, a linger of 1–5ms is the
  knob to add, but the opportunistic rule needs none to batch under load, which is when it matters.

## Expected effect

**Ack turns.** In the 81-stream runs:
- Ack turns per second on the queue actor fall from up to one per stream with acks ready to about
  one per gateway pod per flush round trip.
- The batch size rises from at most a window (5–20) to everything the gateway saw in a round trip,
  which is tens to hundreds.
- Turn time saved goes to refills, and to renewals until those are aggregated too.

**Settle timeouts.** At most 3 ack calls are queued at once, not up to 81, so an ack waits behind far
fewer turns. Timeouts and the redeliveries they cause should drop sharply, and so should cancelled
actor calls (#10835).

**What it doesn't change:**
- the actor's own per-turn cost
- `DequeueLocked` refills: each stream still refills itself
- `ExtendLockBatch` renewals: one per stream per tick

Throughput gains are bounded by how much of the actor's time acks take now. Measuring that is part of
the plan.

## Risks

- **A larger blast radius per failure.** One timed-out flush fails acks from many streams at once,
  where today it fails one stream's window.
  - *Mitigation:* fewer queued calls should make timeouts rarer, which is the point of the change. The
    failure is also no worse per lock: those locks expire and are redelivered as they would be now.
  - *Measure:* settle timeouts in A/B.
- **Head-of-line blocking within a gateway.** A slow flush makes every stream on that gateway wait to
  settle that queue. Each was already waiting on the same actor, so the wait moves, it doesn't grow.
- **A large batch is a long turn.** 1,000 acks is 1,000 lock reads and removals in one transaction.
  - *Mitigation:* keep a cap, at 1,000 to begin with. Lower it if measurement shows long ack turns
    delaying refills or renewals.
- **Memory.** One small entry per active queue per gateway. Idle entries are removed.

## Alternatives considered

- **Bigger prefetch windows, for bigger per-stream batches.** Rejected. Holding more locks means more
  renewals and more expiries. A window of 100 with 1 handler collapsed the test: about 8,100 idle
  prefetched locks were renewed every 10s. See "Prefetch sizing".
- **Batching acks in the SDK.** It doesn't help: the gateway already batches each stream's acks, and
  an SDK only sees its own stream.
- **Routing a queue's streams to one gateway pod.** That gives a single aggregator per queue, but
  needs affinity-aware routing in front of the gateways. It's a possible later step, and the
  aggregator is useful without it.
- **Sharding a queue across several actors.** That removes the single-actor ceiling altogether, but
  ordering would hold only within each shard, not across the queue. It's an architectural change, and
  orthogonal to this one.

## Later

These are separate proposals or phases, each measured on its own:

1. **Aggregate renewals.** Merge every stream's `ExtendLockBatch` tick for a queue into one call per
   gateway pod, grouped by TTL. Renewals scale with locks held, so this is the next biggest per-stream
   cost.
2. **Route unary acks through the aggregator.** `POST /queue/{id}/acknowledge` and gRPC
   `Acknowledge`/`AcknowledgeBatch` would share it, so polling consumers benefit too. This needs care
   with per-call timeouts and with the readiness rules in
   [readiness-and-retries.md](readiness-and-retries.md).
3. **Aggregate refills.** Merge `DequeueLocked` calls across streams and split the items between them.
   This is harder: it means sharing out items fairly, and writing to each stream's window.

## Prefetch sizing (context)

The same tests showed the window must stay small whatever happens to acks. Azure Service Bus
recommends a prefetch of at most about `lock duration ÷ 3 × messages per second` per receiver. That's
20× the processing rate with its default 60s lock, and it defaults prefetch to 0
([performance best practices](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-performance-improvements)).
With a 30s lock and about 1.7 messages a second per stream across 81 streams, that gives a window of
about 17.

DaprMQ currently renews every delivered lock, prefetched or not, so the window drives renewal cost
directly. The aggregator assumes small windows and makes many streams cheap. It doesn't make large
windows safe.

## Plan

1. **Aggregator.** Add `QueueAckAggregator`, a gateway singleton taking `IQueueActorInvoker`, with
   unit tests on a mocked invoker:
   - concurrent submitters on one queue → one call in flight, outcomes routed back to the right
     submitter
   - submitters on different queues → independent flushes
   - more than 1,000 pending → split into calls of at most 1,000
   - a whole-call failure or timeout → `SETTLE_FAILED` with a reason for every lock in the batch
   - a stream that ends after submitting → its acks are still applied
   - an idle queue → its entry is removed
2. **Consume.** Switch the settler to the aggregator behind `DAPRMQ_CONSUME_ACK_AGGREGATION`. Run the
   existing Consume tests in both modes, plus `./build-and-test.sh`.
3. **Measurement.** An A/B on kind, aggregation off then on, with identical runs. Use the 81 × 10
   window-20 and 81 × 1 window-5 runs above, plus 10 × 10 window 20 as a low fan-in control, all with
   the queue reset and the actor hosts restarted before each. Record:
   - drain time and messages a second
   - `AcknowledgeBatch` calls a minute and their batch sizes, from the sidecar's
     `dapr_http_client_completed_count` and a gateway log or metric of batch size
   - settle timeouts in the gateway logs
   - guard held/dropped counts and desyncs, which must stay 0
4. **Decision.** Default the aggregator on if it cuts settle timeouts and doesn't reduce throughput
   in any run. Then decide whether renewals are worth aggregating, from the same measurements.
