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

This proposal gives every message an expiry: the producer's time to live, or a broker-wide default
of one year when the producer doesn't set one.

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

- **Default:** omitted means the broker default, `DAPRMQ_DEFAULT_MESSAGE_TTL_SECONDS` (default
  31,536,000, which is 365 days). A per-queue or per-topic default can come later. Every message gets
  an expiry; there is no "never expires".
- **Validation (controller):** a positive integer, at most 10 years (315,360,000 seconds). The
  broker default must be within the same limit, checked at startup.
- **Delivered items** gain `expiresAt` (Unix seconds) on every dequeue response, consume delivery and
  HTTP sink post, so a consumer can tell how stale an item is.

The name avoids `ttl-seconds`, which is already the lock TTL header on dequeue.

### Absolute expiry, stamped once

`EnqueueItem` gains `ExpiresAt` (Unix seconds), computed by the API server as *now + time to live*,
using the broker default when the producer gave none. Actors never compute it or apply the default.

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

`QueueSegmentItem` and `LockState` gain `MessageExpiresAt`. `LockState` already has `ExpiresAt` for
the lock itself, so the new field is named to keep them apart. Every item enqueued through the API
server has one. The field stays nullable, and null means "never expires", only so that items already
queued, and state planted by tests, keep working. No migration (pre-v1).

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

**What happens to a removed item.** A broker-wide setting decides,
`DAPRMQ_DEADLETTER_ON_EXPIRY` (default `false`, as ASB's `EnableDeadLetteringOnMessageExpiration`).
A per-queue override can come later.

- **Off (default):** the item is dropped, and the actor logs the count at Information level.
- **On:** the item is dead-lettered. Every expired item a turn removes goes to `{id}-deadletter` in
  one `Enqueue` call, made before the local commit, as dead-lettering does today
  ([QueueActor.cs:991](../server/src/DaprMQ/QueueActor.cs#L991)). A failure leaves the items in
  place for the next turn, so an item may be dead-lettered twice but never lost. Each gets the
  dead-letter expiry described below, and a shared topic blob is copied as described there.
- **Never from a dead-letter queue.** An expired item in a dead-letter queue is always dropped,
  whatever the setting. See [Dead-letter queues never dead-letter](#dead-letter-queues-never-dead-letter).
- The same rule applies on every path in the table above: dequeue, lock sweep, nack and session lease
  lapse.

### Dead-letter queue

Dead-lettered items expire after a broker-wide period, `DAPRMQ_DEADLETTER_TTL_SECONDS` (default
2,419,200, which is 28 days). A per-queue or per-topic override can come later.

- **Every dead-lettered item gets the same rule:** its `MessageExpiresAt` becomes *dead-letter time +
  28 days*, replacing its original expiry. That includes items dead-lettered because they expired.
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

### Dead-letter queues never dead-letter

An item in a dead-letter queue can't move to another dead-letter queue under any circumstances. Today
a dead-letter queue is an ordinary `QueueActor` whose id ends in `-deadletter`
([QueueActor.cs:192](../server/src/DaprMQ/QueueActor.cs#L192)). Three paths can therefore create
`{id}-deadletter-deadletter`, and expiry would add a fourth. ASB doesn't allow dead-lettering from a
dead-letter queue either.

A `QueueActor` whose id ends in `DeadLetterActorIdSuffix` handles each path as follows. This includes
a session queue's dead-letter queue, `{queueId}-session-{sessionId}-deadletter`.

| Path | In a dead-letter queue |
|---|---|
| Expiry | The item is dropped, whatever `DAPRMQ_DEADLETTER_ON_EXPIRY` says. |
| Explicit `DeadLetter` (HTTP, gRPC, consume stream frame) | Rejected with a new error code, `DEADLETTER_NOT_ALLOWED`: HTTP 400, gRPC `FailedPrecondition`, a `ConsumeSettleFailed` frame on a consume stream. The lock is left as it was. |
| Lock sweep or nack past `MaxDeliveryCount` | The item is restored to its position as usual, and `MaxDeliveryCount` isn't applied. It still expires after 28 days. |

Dropping on `MaxDeliveryCount` was rejected: it would silently lose an item that someone is still
trying to process. Without the cap a poison item can be redelivered repeatedly, but only until its
28-day expiry.

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
  long, so an item still being worked on 24 hours after it expires is an
  [accepted risk](#accepted-risks).
- **Claim tokens** for an item with an expiry are issued to expire no later than `reapAt`, so a token
  never outlives its blob.
- **Every topic publish has an expiry,** because of the broker default, so every topic publish over
  the offload threshold can use a shared blob.
- **A shared blob is kept until its message expires,** even if every subscriber finished in seconds.
  With the one-year default, that's a year of blob storage per large topic message, an
  [accepted risk](#accepted-risks). A producer that wants the blob deleted sooner sets a shorter time
  to live.
- **Dead-letter on expiry copies the blob late.** An expired item is only dead-lettered when a dequeue
  reaches it. If that's more than 24 hours after it expired, the shared blob is gone and the item is
  dead-lettered with `PAYLOAD_EXPIRED`. This is an [accepted risk](#accepted-risks).

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
- **Behaviour change:** messages that today never expire will expire after a year by default.
- **Shared topic blob storage** lasts for the message's full time to live plus 24 hours, a year by
  default, regardless of how fast subscribers consume.
- **A deep run of expired items** costs one extra actor call per `SweepBatchSize` items removed.

## Tests (TDD order, per CLAUDE.md)

1. **Integration** (HTTP and gRPC):
   - An item enqueued with a 1-second time to live, dequeued after 2 seconds: 204 empty.
   - An item locked before expiry can be acknowledged after it.
   - A topic publish with a time to live reaches every subscriber with the same `expiresAt`.
   - Dequeued items carry `expiresAt`.
2. **Controller / gRPC service** (mocked actor):
   - `timeToLiveSeconds` of 0, negative or over 10 years: 400 / `InvalidArgument`.
   - `ExpiresAt` is stamped from the controller's clock; an omitted time to live uses
     `DAPRMQ_DEFAULT_MESSAGE_TTL_SECONDS`.
   - A broker default over 10 years fails startup.
   - An `ExpiredMore` result is retried until items, empty or locked come back.
   - `DEADLETTER_NOT_ALLOWED` maps to HTTP 400 and gRPC `FailedPrecondition`.
3. **Actor** (mocked state):
   - Dequeue skips and removes expired items at the head, in one save.
   - Removes at most `SweepBatchSize` expired items, then returns `ExpiredMore`.
   - Lock sweep, nack and session lease lapse drop expired items instead of restoring them.
   - An expired item doesn't increase `DeliveryCount`.
   - With `DAPRMQ_DEADLETTER_ON_EXPIRY` off, expired items are dropped; on, they are dead-lettered in
     one call per turn, before the local commit, and a failed call leaves them in place.
   - An expired item in a dead-letter queue is dropped even with the setting on.
   - In a dead-letter queue, `DeadLetter` returns `DEADLETTER_NOT_ALLOWED` and leaves the lock; a lock
     sweep or nack past `MaxDeliveryCount` restores the item instead of dead-lettering it. Also for a
     session queue's dead-letter queue.
   - No path ever enqueues to an id ending `-deadletter-deadletter`.
   - Dead-lettering sets `MessageExpiresAt` to dead-letter time + `DAPRMQ_DEADLETTER_TTL_SECONDS`,
     with or without an original expiry.
   - Dead-lettering a shared-blob item schedules the copy's deletion, then copies, then enqueues to
     the dead-letter queue, in that order; the original blob's schedule is untouched.
   - Dead-lettering a shared-blob item whose blob is gone records `PAYLOAD_EXPIRED`.
   - An item with a null `MessageExpiresAt` never expires.
4. **TopicActor:** fan-out passes the publish's `ExpiresAt` to every subscriber unchanged.

## Phases

1. **Server:** models, actor checks, REST, gRPC, topic fan-out.
2. **SDKs:** all five expose a time to live on enqueue and publish, and `expiresAt` on delivered
   items.
3. **Pubsub component:** map `ttlInSeconds`.
4. **Docs:** [API_REFERENCE.md](../docs/API_REFERENCE.md), [ARCHITECTURE.md](../docs/ARCHITECTURE.md),
   and a line in `CLAUDE.md` Domain Knowledge.

## Decisions

1. **Dead-letter on expiry:** a broker-wide setting, `DAPRMQ_DEADLETTER_ON_EXPIRY`, off by default as
   in ASB. Per-queue overrides later.
2. **Default time to live:** a broker-wide default of one year,
   `DAPRMQ_DEFAULT_MESSAGE_TTL_SECONDS`, overridable per message up to 10 years. Per-queue and
   per-topic defaults later.
3. **Dead-lettered items expire** after `DAPRMQ_DEADLETTER_TTL_SECONDS`, 28 days by default. Per-queue
   and per-topic overrides later.
4. **No background sweep;** see [Queue depth](#queue-depth).
5. **Dead-letter queue state storage is deferred.** Blobs in a dead-letter queue are deleted on
   schedule, but its expired items are removed only when a dequeue reaches them, and a dead-letter
   queue is often never dequeued. Its state store entries can stay past 28 days, never delivered but
   taking space and counting in its depth. This is the one case where a sweep might earn its cost; it
   will be decided once dead-letter queues are seen to grow.
6. **Dead-letter queues never dead-letter,** by any path; see
   [Dead-letter queues never dead-letter](#dead-letter-queues-never-dead-letter).

## Accepted risks

All three come from deleting a shared topic blob 24 hours after its message expires, rather than when
subscribers finish with it. The first two need an item to be held, or left in the queue, more than a
day past its expiry, which is expected to be rare because most times to live are long.

1. **A consumer holds an item more than 24 hours past its expiry.** Locks can be extended, and consume
   streams renew them for as long as the stream is open, so nothing stops this. The shared blob is
   deleted on schedule:
   - an offloaded item (`kind: "item"`) is unaffected, because its payload was put back inline when it
     was dequeued;
   - an object's claim token returns 404;
   - dead-lettering the item records `PAYLOAD_EXPIRED`.
2. **Late dead-lettering on expiry.** With `DAPRMQ_DEADLETTER_ON_EXPIRY` on, an expired item is
   dead-lettered only when a dequeue reaches it, because removal is lazy. If that is more than 24
   hours after it expired, its shared blob is already gone, and it is dead-lettered as
   `PAYLOAD_EXPIRED`: the dead-letter queue shows the item, but not its payload. A queue that's
   consumed regularly reaches its expired items well within 24 hours. A queue left idle for more than
   a day doesn't.
3. **Shared topic blobs are kept for the message's full time to live.** A shared blob is deleted 24
   hours after its message expires, however quickly subscribers consume it. Under the one-year
   default, that's a year of blob storage per large topic message. A producer that wants the blob
   cleaned up sooner sets a shorter time to live on the message, which brings its deletion forward.
