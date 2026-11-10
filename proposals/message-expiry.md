# Message expiry

**Status:** proposed. Not yet implemented. Needed by the topic design in
[large-payload-offload.md](large-payload-offload.md) (open question 2).

## Context

A queued item lives until a consumer takes it. DaprMQ has TTLs on locks, session leases, idempotency
markers and claim tokens, but not on messages
([QueueActor.cs:78](../server/src/DaprMQ/QueueActor.cs#L78) holds only `ItemJson`, `Sequence` and
`DeliveryCount`). That has two costs:

- **Producers can't say "worthless after X".** A price quote, a one-time code, or a notification that
  is stale after an hour sits in the queue, and is delivered, however late it is.
- **Nothing bounds how long a payload must be kept.** Large-payload offload needs that to share one
  blob between topic subscribers: if every subscriber's copy expires at the same moment, the blob can
  be deleted then, with no reference counting.

This proposal adds a per-message time to live, set at enqueue or publish.

## Proposal

The producer gives a time to live in seconds. The API server turns it into an absolute expiry time
once, when it receives the request. The expiry travels with the item through every move: lock,
restore, nack, dead-letter, topic fan-out and scheduled delivery. An item past its expiry is never
delivered. It is removed lazily, when a dequeue or sweep reaches it, as lock expiry is today.

### API

| Transport | Field |
|---|---|
| HTTP `enqueue` | `timeToLiveSeconds` per item (beside `priority`, `idempotencyKey`, `sessionId`) |
| HTTP `enqueue-object` / `enqueue-stream` | `time-to-live-seconds` header |
| HTTP topic `publish` | `timeToLiveSeconds` per item |
| gRPC | `optional int32 time_to_live_seconds = 5` on `EnqueueItem` |
| Dapr pubsub (`pubsub.daprmq`) | Dapr's standard `ttlInSeconds` publish metadata |

- **Validation (controller):** a positive integer, at most 10 years. Omitted means the message never
  expires, as today.
- **Delivered items** gain `expiresAt` (Unix seconds, null when there is none) on every dequeue
  response, consume delivery and HTTP sink post, so a consumer can tell how stale an item is.

The name avoids `ttl-seconds`, which is already the lock TTL header on dequeue.

### Absolute expiry, stamped once

`EnqueueItem` gains `ExpiresAt` (Unix seconds, nullable), computed by the API server as *now + time
to live*. Actors never compute it.

- **Topic fan-out shares one expiry.** `TopicActor` passes the item's `ExpiresAt` to every
  subscriber's `Enqueue` unchanged. A subscriber reached late, after retries or a tripped circuit
  breaker, still gets the publish-time expiry, so every copy expires at the same moment. This is what
  the shared topic blob depends on.
- **Scheduled delivery** (proposed on `feature/scheduled-delivery`): expiry is *deliverAt + time to
  live*, so a message can't expire before it is due. `ScheduleActor` forwards `ExpiresAt` unchanged.
- **Clock skew:** the API server's clock sets the expiry and an actor's clock checks it. Skew between
  nodes is seconds at most, which is noise against TTLs of minutes or more. Anything that deletes data
  at expiry (the blob reaper) adds a grace period; see [Offloaded payloads](#offloaded-payloads).

### Storage

`QueueSegmentItem` and `LockState` gain `MessageExpiresAt` (nullable). `LockState` already has
`ExpiresAt` for the lock itself, so the new field is named to keep them apart. Items enqueued without
a time to live store null and behave exactly as today. No migration (pre-v1).

### Where expiry is checked

Expiry applies to items **waiting in the queue**. Once an item is locked, the consumer holds it and
can acknowledge, extend, nack or dead-letter it normally, even past expiry, as Azure Service Bus
(ASB) does. Expiry is checked again whenever the item would go back into the queue.

| Path | Behaviour |
|---|---|
| `Dequeue` / `DequeueLocked` (and everything built on them: consume streams, HTTP sink) | While popping the head, an expired item is removed and the pop continues. |
| Lock expiry sweep ([QueueActor.cs:671](../server/src/DaprMQ/QueueActor.cs#L671)) | An expired item is removed instead of restored. |
| `Nack` | An expired item is removed instead of restored. |
| Session lease lapse ([QueueActor.cs:837](../server/src/DaprMQ/QueueActor.cs#L837)) | Expired items are removed instead of bulk-restored to the front. |
| `Acknowledge`, `ExtendLock`, `DeadLetter` | Unchanged. The consumer holds the item. |

Removal happens in the same actor turn as the dequeue, staged and saved with the turn's one
`SaveStateAsync`. An expired item doesn't count towards `MaxDeliveryCount`.

**Bounded work per turn.** A long run of expired items at the head could make one dequeue walk
thousands of items. Each turn removes at most `SweepBatchSize` expired items. If it reaches that
limit without finding a live item, it commits what it removed and returns a new `ExpiredMore` flag
with no items. The controller, gRPC service and consume loop call again straight away, each call a
new atomic turn, and the client sees an ordinary dequeue. An empty or locked result never sets the
flag.

**What happens to a removed item.** It is dropped, and the actor logs the count at Information level.
Dead-lettering on expiry is an open question.

### Dead-letter queue

Dead-lettered items expire after a broker-wide period, `DAPRMQ_DEADLETTER_TTL_SECONDS` (default
2,419,200, which is 28 days). A per-queue or per-topic override can come later.

- **Every dead-lettered item gets the same rule:** its `MessageExpiresAt` becomes *dead-letter time +
  28 days*, replacing any original expiry. Items enqueued without a time to live get it too.
- **ASB differs.** It applies no time to live in the dead-letter queue and never cleans it up on its
  own ([docs](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-dead-letter-queues)).
  A bounded default is deliberate, so dead-letter queues don't grow forever.
- The admin console shows the expiry on dead-lettered items.

**Shared topic blobs are copied on dead-letter.** The original blob is shared with other subscribers
and is deleted at its own `reapAt`, so the dead-lettered item gets its own copy:

1. Schedule the copy's deletion at the item's new expiry plus the grace period.
2. Copy the blob: `DownloadAsync` piped into `UploadAsync`, streamed, never buffered whole. The
   Binary Store has no copy operation.
3. Enqueue the item to the dead-letter queue, with an envelope pointing at the copy and `reapAt` set
   to the copy's deletion time.
4. Commit locally, as dead-lettering does today
   ([QueueActor.cs:991](../server/src/DaprMQ/QueueActor.cs#L991)).

A crash at any step leaves at most a copy whose deletion is already scheduled, so nothing leaks. The
original blob is left alone and is deleted on its own schedule.

- **The copy runs inside the queue actor's turn,** in an explicit `DeadLetter`, a nack past
  `MaxDeliveryCount`, or a lock sweep. A large copy blocks that queue for its duration. Dead-lettering
  is rare, so this is accepted.
- **If the original blob is already gone,** which needs a consumer to hold the item more than 24
  hours past its expiry (see [Offloaded payloads](#offloaded-payloads)), the item is dead-lettered
  without its payload: the envelope is kept with a `PAYLOAD_EXPIRED` marker, so the dead-letter queue
  still shows what was lost.

### Offloaded payloads

How expiry interacts with [large-payload offload](large-payload-offload.md):

- **Consumer-owned blobs** (plain queues, no `reapAt`): dropping an expired item schedules its blob's
  reap, the same as a plain dequeue does today. Otherwise the blob would leak.
- **Shared topic blobs** (`reapAt` on the envelope): `reapAt` is the message's expiry plus a grace
  period, `DAPRMQ_BLOB_EXPIRY_GRACE_SECONDS` (default 86,400, which is 24 hours). It covers clock skew,
  a consumer still working on a locked item after it expires, and a claim token redeemed late.
  Dropping an expired item schedules nothing; the blob's deletion was scheduled at publish.
- **Locks aren't capped at `reapAt`.** A consumer can extend a lock, and consume streams renew locks
  for as long as the stream is open, so an item can be held past `reapAt`. Most times to live are
  long, so an item still being worked on 24 hours after it expires is accepted as rare. If it happens:
  - an offloaded item (`kind: "item"`) is unaffected, because its payload was put back inline when it
    was dequeued;
  - an object's claim token returns 404;
  - dead-lettering it records `PAYLOAD_EXPIRED` (see [Dead-letter queue](#dead-letter-queue)).
- **Claim tokens** for an item with an expiry are issued to expire no later than `reapAt`, so a token
  never outlives its blob.
- **Only messages with an expiry get a shared blob.** A topic publish over the offload threshold with
  no time to live is stored inline, so no shared blob ever lacks a deletion time. No topic-wide
  default is needed for this.

### Queue depth

`QueueMetadata.Count`, and so queue depth, the admin console and KEDA, includes expired items until a
dequeue reaches them. A queue whose items have all expired reports a depth until a consumer drains
it. KEDA scaling up a consumer for that is what clears them. The admin console marks expired items
in its message view.

**Decision: no background sweep.** Expiry is enforced only when a dequeue reaches an item, and
expired items count towards depth until then.
- **Depth corrects itself.** Under KEDA, a depth made of expired items scales up a consumer, and its
  first dequeue removes them. The cost is one wasted scale-up.
- **Storage is the only lasting cost,** and only for a queue nobody dequeues from again. Lock expiry
  makes the same trade-off.
- **There's no good trigger for a sweep.** Dapr deactivating an idle actor is the obvious signal,
  but `OnDeactivateAsync` isn't a reliable place to do work and save state. A reminder armed at
  enqueue for the earliest expiry would put scheduler load on every queue that uses a time to live,
  which is what lazy lock expiry was designed to avoid.

### Not changed

- Segment layout, `Sequence`, FIFO order and priority order. Expiry never reorders.
- Idempotency markers keep their own TTL.
- Session leases and lock TTLs.

## Limits and costs

- **Lazy removal.** Expired items take state store space until a dequeue reaches them. A queue nobody
  dequeues from never frees them. That matches lock expiry, which is also swept only on access; see
  [Queue depth](#queue-depth) for why there is no background sweep.
- **Per-item cost:** one nullable double in each segment item and lock.
- **A deep run of expired items** costs one extra actor call per `SweepBatchSize` items removed.

## Tests (TDD order, per CLAUDE.md)

1. **Integration** (HTTP and gRPC):
   - An item enqueued with a 1-second time to live, dequeued after 2 seconds: 204 empty.
   - An item locked before expiry can be acknowledged after it.
   - A topic publish with a time to live reaches every subscriber with the same `expiresAt`.
   - Dequeued items carry `expiresAt`.
2. **Controller / gRPC service** (mocked actor):
   - `timeToLiveSeconds` of 0, negative or over 10 years: 400 / `InvalidArgument`.
   - `ExpiresAt` is stamped from the controller's clock.
   - An `ExpiredMore` result is retried until items, empty or locked come back.
3. **Actor** (mocked state):
   - Dequeue skips and removes expired items at the head, in one save.
   - Removes at most `SweepBatchSize` expired items, then returns `ExpiredMore`.
   - Lock sweep, nack and session lease lapse drop expired items instead of restoring them.
   - An expired item doesn't increase `DeliveryCount` or reach the dead-letter queue.
   - Dead-lettering sets `MessageExpiresAt` to dead-letter time + `DAPRMQ_DEADLETTER_TTL_SECONDS`,
     with or without an original expiry.
   - Dead-lettering a shared-blob item schedules the copy's deletion, then copies, then enqueues to
     the dead-letter queue, in that order; the original blob's schedule is untouched.
   - Dead-lettering a shared-blob item whose blob is gone records `PAYLOAD_EXPIRED`.
   - An item without an expiry behaves exactly as today.
4. **TopicActor:** fan-out passes the publish's `ExpiresAt` to every subscriber unchanged.

## Phases

1. **Server:** models, actor checks, REST, gRPC, topic fan-out.
2. **SDKs:** all five expose a time to live on enqueue and publish, and `expiresAt` on delivered
   items.
3. **Pubsub component:** map `ttlInSeconds`.
4. **Docs:** [API_REFERENCE.md](../docs/API_REFERENCE.md), [ARCHITECTURE.md](../docs/ARCHITECTURE.md),
   and a line in `CLAUDE.md` Domain Knowledge.

## Open questions

1. **Dead-letter on expiry.** ASB has a per-entity `EnableDeadLetteringOnMessageExpiration`, off by
   default. DaprMQ could add the same, but it needs a per-queue config call (today only
   `ConfigureDedup` exists, used by `TopicActor`), and each dead-lettered batch is a cross-actor call
   inside the dequeue turn. It would fit the [Dead-letter queue](#dead-letter-queue) rule as is: the
   item gets a fresh 28-day expiry when it's dead-lettered. Recommendation: drop only, until someone
   needs it.
2. **Per-queue default time to live.** ASB has `DefaultMessageTimeToLive` per queue, topic and
   subscription, and caps a message's time to live at it. Recommendation: per-message only for now.
   The shared topic blob doesn't need a default, because untimed publishes stay inline.
3. **Dead-letter queue state storage.** Blobs in the dead-letter queue are deleted on schedule, but
   expired items are removed only when a dequeue reaches them, and a dead-letter queue is often never
   dequeued. Its state store entries would stay past 28 days: never delivered, but taking space and
   counting in its depth. This is the one case where a sweep might earn its cost (see
   [Queue depth](#queue-depth)). Recommendation: decide once dead-letter queues are seen to grow.
