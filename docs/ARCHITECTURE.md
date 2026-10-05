# Architecture Overview

Understanding how QueueActor works under the hood.

## Core Concepts

### Dapr Actors

QueueActor is built on Dapr's Virtual Actor pattern:

- **Virtual Actors**: Actors are automatically activated on first use, deactivated when idle
- **Single-Threaded**: Each actor instance processes one operation at a time (no race conditions)
- **Location Transparent**: Actors can be on any node in the cluster
- **Persistent State**: Actor state survives restarts and migrations

### Actor Identity

Each actor instance is uniquely identified by:
- **Actor Type**: `"QueueActor"`
- **Actor ID**: User-defined string (e.g., `"user-123-tasks"`, `"email-queue"`)

Example: Two queues with different IDs are completely independent:
```csharp
var queue1 = ActorProxy.Create<IQueueActor>(new ActorId("queue-1"), "QueueActor");
var queue2 = ActorProxy.Create<IQueueActor>(new ActorId("queue-2"), "QueueActor");
```

**Derived/suffixed actor IDs**: several features address a plain `QueueActor` instance under an id derived from another actor's id rather than a user-chosen one — `{id}-deadletter` (dead-letter queue), `{id}-sink` (HTTP sink), `{topicId}-sub-{subscriberId}` (topic subscriber), `{queueId}-session-{sessionId}` (session, see [SESSIONS_IMPLEMENTATION.md](SESSIONS_IMPLEMENTATION.md)). All are still ordinary `QueueActor` instances addressed like any other — the suffix is just a naming convention for predictable discovery, not a different actor type.

## State Management

### State Store

Actor state is persisted in a Dapr state store component:

```
┌─────────────────┐
│  QueueActor   │
│  (actor-123)    │
└────────┬────────┘
         │
         │ save_state()
         ▼
┌─────────────────┐
│  State Manager  │
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│  State Store    │
│  (PostgreSQL)   │
└─────────────────┘
```

### State Schema

Each actor uses a **segmented queue architecture** where priority queues are split into fixed-size segments (default: 100 items per segment):

**Segment Keys**: `queue_0_seg_0`, `queue_0_seg_1`, `queue_1_seg_0`, etc.
**Value**: JSON array of strings (max 100 JSON items per segment)

**Metadata Key**: `metadata`
**Value**: JSON object with config and queue metadata including segment pointers

Example state for actor "my-queue" with 250 items in priority 0:

```json
// queue_0_seg_0 (first segment, being dequeued from)
[
  {"task": "urgent_email", "user_id": 123},
  {"task": "critical_alert", "severity": "high"},
  ... // 98 more items
]

// queue_0_seg_1 (second segment, full)
[
  {"task": "process_data", "id": 101},
  ... // 99 more items
]

// queue_0_seg_2 (third segment, being enqueued to)
[
  {"task": "final_item", "id": 250}
]

// metadata
{
  "config": {
    "segment_size": 100,
    "buffer_segments": 1
  },
  "queues": {
    "queue_0": {
      "metadata": {
        "count": 250,
        "head_segment": 0,
        "tail_segment": 2
      }
    }
  }
}
```

**Segment Pointers**:
- `head_segment`: Segment to dequeue from (oldest items)
- `tail_segment`: Segment to enqueue to (newest items)
- `count`: Total items across all segments
- `head_offloaded_segment` (optional): First segment number in offloaded range (v4.1+)
- `tail_offloaded_segment` (optional): Last segment number in offloaded range (v4.1+)

### State Operations

**Enqueue Operation:**
1. Validate items array (1-1000 items, non-empty ItemJson, priority >= 0 for each)
2. Group items by priority
3. For each priority group, load metadata and get tail segment number
4. For each item in group:
   - Load tail segment (e.g., `queue_0_seg_2`) from state store
   - If segment is full (100 items), allocate new segment (increment tail pointer)
   - Append item to tail segment
5. Update metadata (count, tail pointer) for each priority
6. Save all segments and metadata atomically with single SaveStateAsync()

**Dequeue Operation:**
1. Load metadata to determine which priorities have items
2. Sort priority keys numerically (0, 1, 2, ...)
3. For each priority in order, load head segment (e.g., `queue_0_seg_0`)
4. Dequeue single item from front of segment
5. If segment becomes empty:
   - If more segments exist: increment head pointer, don't save empty segment
   - If last segment: delete queue metadata
6. Save updated segment (if not empty) and metadata

**Benefits of Segmentation:**
- **Memory**: Load max 100 items per operation instead of entire queue
- **Network**: Serialize max 100 items instead of N items per save
- **Performance**: Dequeue becomes O(1) instead of O(N) for list slicing

### Segment Offloading (v4.1+)

**Segment Offloading** is a memory optimization that unloads "middle" full segments from the actor's in-memory cache while keeping them in the permanent state store. This reduces active memory footprint while maintaining FIFO guarantees and performance.

**Architecture:**

```
┌─────────────────────────────────────────────────────────┐
│        Actor In-Memory Cache (StateManager)             │
├─────────────────────────────────────────────────────────┤
│  queue_0_seg_0 (head - active)        [100 items]      │
│  queue_0_seg_1 (buffer)               [100 items]      │
│  queue_0_seg_49 (tail - active)       [50 items]       │
└─────────────────────────────────────────────────────────┘
                         │
                         │ unload/load (UnloadStateAsync)
                         ▼
┌─────────────────────────────────────────────────────────┐
│    Permanent State Store (PostgreSQL, Redis, etc.)      │
├─────────────────────────────────────────────────────────┤
│  queue_0_seg_0        [100 items]  ← Always present     │
│  queue_0_seg_1        [100 items]  ← Always present     │
│  queue_0_seg_2        [100 items]  ← Unloaded from cache│
│  queue_0_seg_3        [100 items]  ← Unloaded from cache│
│  ...                                                     │
│  queue_0_seg_48       [100 items]  ← Unloaded from cache│
│  queue_0_seg_49       [50 items]   ← Always present     │
└─────────────────────────────────────────────────────────┘

Memory Usage: ~250 items instead of ~4,950 items (95% reduction)
```

**Key Insight:** Segments stay at their regular keys (`queue_{priority}_seg_{segmentNum}`). The `UnloadStateAsync()` method removes them from the actor's in-memory tracking/cache but leaves them in the permanent store. Dapr automatically loads them back when accessed via `TryGetStateAsync()`.

**When Segments Are Offloaded:**

A segment is eligible for offload when:
1. Segment is full (100 items)
2. `segment_num > head_segment + buffer_segments`
3. `segment_num < tail_segment`

**Configuration:**
- `buffer_segments` (default: 1): Number of full segments to keep in memory between head and offloaded segments
  - Higher values = more memory, less latency
  - Lower values = less memory, occasional load latency

**Offload Flow** (during Enqueue):
1. After successful enqueue, check if any segments are eligible
2. For each eligible segment:
   - Call `StateManager.UnloadStateAsync(segmentKey)` to unload from memory
   - Extend offloaded range (`head_offloaded_segment`/`tail_offloaded_segment`) in metadata
3. Continue (non-blocking on failure)

**Note**: Offloaded segments are always contiguous, so they're stored as a range (min/max) in metadata rather than a list, preventing unbounded metadata growth.

**Load Flow** (during Dequeue):
1. Before accessing head segment, check if any offloaded segments need loading
2. Load segments where `segment_num <= head_segment + buffer_segments`
3. For each segment to load:
   - Call `StateManager.TryGetStateAsync(segmentKey)` - Dapr hydrates from permanent store automatically
   - Shrink offloaded range (increment `head_offloaded_segment`) in metadata

**Benefits:**
- **Memory**: Reduces from O(N items) to O(buffer_segments × 100)
- **Example**: 10,000 item queue uses ~300 items in memory (97% reduction)
- **Transparent**: No API changes - offloading happens automatically
- **Simplicity**: Single state key namespace, no manual cleanup needed
- **Graceful**: Failures degrade to full memory mode (non-blocking)

**Trade-offs:**
- **Latency**: Loading segments from state store adds ~10-50ms per load
- **Frequency**: Load happens once per 100 dequeues when entering buffer zone
- **Tunable**: Increase `buffer_segments` to reduce load frequency

## Actor Lifecycle

### Activation

When an actor is first accessed, metadata is initialized automatically through the `GetMetadataAsync()` helper:

```csharp
private async Task<Dictionary<string, object>> GetMetadataAsync()
{
    var result = await StateManager.TryGetStateAsync<Dictionary<string, object>>("metadata");
    if (result.HasValue)
    {
        return result.Value;
    }

    return new Dictionary<string, object>
    {
        ["config"] = new Dictionary<string, object>
        {
            ["segment_size"] = MaxSegmentSize,
            ["buffer_segments"] = 1
        },
        ["queues"] = new Dictionary<string, object>()
    };
}
```

### Deactivation

Dapr automatically deactivates idle actors based on `actorIdleTimeout` (default: 1 hour).

When reactivated, state is loaded from state store automatically.

## Concurrency Model

### Single-Threaded Actor

Each actor instance processes one request at a time:

```
Request 1 → Enqueue()  ──▶ [Processing...] ──▶ Response
Request 2 → Dequeue()  ──▶ [Queued...]     ──▶ [Processing...] ──▶ Response
Request 3 → Enqueue()  ──▶ [Queued...]                ──▶ [Processing...] ──▶ Response
```

This eliminates race conditions - no locks needed!

### Multiple Actor Instances

Different actor IDs run in parallel:

```
Actor "queue-1" → Enqueue()  ─┬─▶ [Processing independently]
Actor "queue-2" → Dequeue()  ─┼─▶ [Processing independently]
Actor "queue-3" → Enqueue()  ─┴─▶ [Processing independently]
```

## Locks and Competing Consumers

### Locked dequeue

`DequeueLocked` (`require-ack: true` over REST) takes items off the queue and parks each in its own lock, `{lockId}-lock`,
which holds the item itself (`LockState.ItemJson`). A locked item no longer counts toward the queue's `Count`;
`ActorMetadata.LockCount` tracks how many are in flight. `Acknowledge` deletes the lock, `ExtendLock` pushes its expiry
out, and `DeadLetter` moves the item to `{queueId}-deadletter`.

### Expiry

There are no reminders or timers. Locks are swept lazily at the top of Dequeue/DequeueLocked/SetSessionLease and on
activation, found through the `locks_exp_{bucket}` index (`ActorMetadata.LockExpiryBuckets`). An expired item is
restored to the **position it was taken from**, not the tail: every item carries a monotonic `Sequence` stamped at
enqueue, and the sweep merges reclaimed items back into the head segment in `Sequence` order. Each expiry increments the
item's `DeliveryCount`; past `MaxDeliveryCount` (`DAPRMQ_MAX_DELIVERY_COUNT`, default 10) it is dead-lettered instead.
Lock TTL is clamped to 1–300 seconds.

### Single-consumer vs. competing consumers

| | Default (single consumer) | `allow-competing-consumers: true` |
|---|---|---|
| While any lock is outstanding | further locked dequeues get `423 Locked` | each caller gets its own locks |
| Ordering | strict FIFO processing - one batch in flight at a time | FIFO hand-out, but processing order across consumers is best-effort |
| Use when | one consumer, or order of processing matters | several consumers/replicas share the queue (e.g. scaled by KEDA) |

The default doesn't mean one *item* at a time - a single call with `count: 10` takes 10 locks; it's the *next* caller
that is blocked until they're all resolved. Every SDK exposes the flag (`allowCompetingConsumers` /
`allow_competing_consumers`; see each SDK's `docs/CLIENT_SDK.md`); the gRPC `DequeueLocked` has `allow_competing_consumers`.

### MaxConcurrency (sinks only)

`DequeueLockedRequest.MaxConcurrency` caps the queue's **total** in-flight locks: a call receives at most
`MaxConcurrency - LockCount` items, and gets `MaxConcurrencyReached` (not `Locked`) at zero. It is not a public dequeue
parameter - it is set only through an HTTP sink's `maxConcurrency` (1–100), and the sink always dequeues in competing
mode, so the cap bounds how many concurrent webhook deliveries it makes. Because the cap counts every lock on the queue,
a sink sharing a queue with other consumers is throttled by their locks too.

## Scalability

### Horizontal Scaling

Actors are distributed across app instances using consistent hashing:

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│   App Pod 1  │  │   App Pod 2  │  │   App Pod 3  │
├──────────────┤  ├──────────────┤  ├──────────────┤
│ Actor A, D   │  │ Actor B, E   │  │ Actor C, F   │
└──────────────┘  └──────────────┘  └──────────────┘
       ▲                 ▲                 ▲
       └─────────────────┼─────────────────┘
                         │
                  ┌──────┴──────┐
                  │  Placement  │
                  │   Service   │
                  └─────────────┘
```

### Placement Service

Dapr's placement service:
- Tracks which actors are on which app instances
- Routes requests to correct instance
- Handles actor migration during scaling/failures

### Autoscaling consumers with KEDA

`DaprMQ.Operator` (Helm `operator.enabled`) implements KEDA's
[external scaler](https://keda.sh/docs/latest/scalers/external/) gRPC contract so consumer workloads scale on queue depth.

```
KEDA ──gRPC (externalscaler.proto)──▶ DaprMQ.Operator   (app-id {release}-daprmq-operator)
                                         │ Dapr service invocation
                                         ▼
                                      workers ──▶ POST /internal/queue-depth
                                         │ sidecar actor-state GET (no activation)
                                         ▼
                                      state store
```

**Why depth is read on the workers.** Dapr keys actor state by the *calling* sidecar's app-id
(`{appId}||{actorType}||{actorId}||{key}`), so a `GET /v1.0/actors/{type}/{id}/state/metadata` from any other app-id
silently returns empty (204). The read has no "hosted here" check, though, so any worker can read any queue, and it never
activates the actor - polling doesn't keep idle queues alive. The workers expose that read as an internal endpoint
(`QueueDepthEndpoint`, mapped whenever actors are registered), and the operator calls it.

**Metric.** Messages mode: ready + locked items (+ `{queueId}-deadletter` with `includeDeadLetter: "true"`), summed
across priorities. Locked items always count: they are out of the queue's `Count`, and lock expiry is swept lazily by
the next dequeue, so scaling consumers to zero over a fully locked queue would strand any lock a scaled-in pod left
behind. Sessions mode: number of `{queueId}-session-{id}` actors in the queue's SessionCoordinatorActor directory
holding any ready or locked items.

**Failure.** A failed read is gRPC `Unavailable`, never 0 - reporting 0 would scale consumers to zero during a DaprMQ
outage. Configure the ScaledObject's `fallback` for what should happen instead.

Trigger fields and a full example: [examples/keda/consumer-scaledobject.yaml](../examples/keda/consumer-scaledobject.yaml).

## Integration Patterns

### 1. Direct Actor Invocation

There are two ways to invoke actors directly: **remoting** (interface-based) and **nonremoting** (method name strings).

#### Remoting (Interface-Based)

```csharp
using Dapr.Actors.Client;
using DaprMQ.Interfaces;

var proxy = ActorProxy.Create<IQueueActor>(new ActorId("my-queue"), "QueueActor");
await proxy.Enqueue(new EnqueueRequest
{
    Items = new List<EnqueueItem>
    {
        new EnqueueItem { ItemJson = itemJson, Priority = 0 }
    }
});
```

**Pros:**
- Type-safe with compile-time checking
- IntelliSense support
- Refactoring-friendly

**Cons:**
- Requires shared interface definitions
- Tight coupling between client and actor
- Recompilation needed when interfaces change

#### Nonremoting (Method Name Strings)

```csharp
using Dapr.Actors.Client;
using DaprMQ.Interfaces;  // Only for request/response models

var proxy = ActorProxy.Create(new ActorId("my-queue"), "QueueActor");
var result = await proxy.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
    "Enqueue",
    new EnqueueRequest
    {
        Items = new List<EnqueueItem>
        {
            new EnqueueItem { ItemJson = itemJson, Priority = 0 }
        }
    }
);
```

**Pros:**
- Decoupled from actor interface
- Enables cross-language scenarios
- Aligns with Dapr's HTTP-based actor protocol
- No need to share compiled interfaces

**Cons:**
- Method names are strings (typo risk - mitigated by constants)
- No IntelliSense for method names
- Generic type parameters required for type safety

**Note**: The included API server (DaprMQ.ApiServer) uses the nonremoting approach with method name constants for better maintainability

### 2. REST API

```bash
curl -X POST http://localhost:8002/queue/queue-1/enqueue
```

**Pros:**
- Language-agnostic
- Simple HTTP interface
- Easy to test with curl

**Cons:**
- HTTP overhead
- Requires API server

## Topics (Pub/Sub)

`TopicActor` adds fan-out on top of `QueueActor` delegation: publishing to a topic makes an item
available to every subscriber, and each subscriber is backed by its own independent `QueueActor`
instance - so every consumer group gets full FIFO/lock/DLQ semantics for free, with no new
queue-storage logic. Delivery is both:

- **Pull** - consumers call the existing Dequeue/DequeueLocked/Acknowledge/ExtendLock API against their
  subscriber queue, unchanged.
- **Push** - a subscription's provisioned queue can optionally have an HTTP sink
  registered on it via the existing sink endpoints, reused as-is.

**Storage model**: each published item is written once, keyed by a monotonic sequence number, in
the topic's own state (a shared item log, not a per-subscriber copy). Each subscriber owns only a
cursor into that log. Subscriber-set membership is versioned by generation rather than stamped
onto every item, so a subscription change costs one new generation snapshot, not a rewrite of
every published item.

**Relay**: a single `relay` reminder per topic (never one per subscriber) reads each eligible
subscriber's next batch and enqueues them concurrently via `Task.WhenAll`, so one slow or
unreachable subscriber cannot delay delivery to any other. A per-subscriber circuit breaker
(exponential backoff, then blacklist after a sustained failure streak) keeps a permanently-broken
subscriber from consuming relay resources; recovery is manual only, via `ResetCircuitBreaker`.

**Cleanup**: a `reap-items` reminder deletes items once every subscriber's cursor has passed them;
a per-generation `reap-generation-{id}` reminder deletes a superseded subscriber-set snapshot after
a retention window. Both are bounded by subscriber/generation count, never by total items
published.

See `PUBSUB-PLAN.md` in the repo root for the full design rationale, including alternatives that
were considered and rejected.

## Failure Handling

### State Store Failures

If state store is unavailable:
- Enqueue/Dequeue operations fail and return error
- Actor state manager retries internally
- Caller receives exception after retries exhausted

### Actor Migration

When app instance fails:
1. Placement service detects failure
2. Actor is re-activated on healthy instance
3. State is loaded from state store
4. Operations continue with no data loss

### Exactly-Once Semantics

Dapr actors provide **at-least-once** delivery:
- Operations may be retried on failure
- Implement idempotency in consumers if needed

## Configuration

### Actor Runtime Config

```yaml
actorIdleTimeout: "1h"        # Deactivate after 1 hour of inactivity
actorScanInterval: "30s"      # Check for idle actors every 30s
drainOngoingCallTimeout: "30s"  # Wait 30s for calls during shutdown
drainRebalancedActors: true   # Move actors gracefully during rebalance
```

### State Store Config

```yaml
type: state.postgresql
metadata:
- name: actorStateStore
  value: "true"              # Required for actor state
- name: connectionString
  value: "host=..."          # Database connection
```

### Placement Service

- Runs as separate service (not per-instance)
- Maintains consistent hash ring
- Handles actor distribution and rebalancing

## Comparison to Alternatives

### vs. Redis Queue

**QueueActor:**
- ✅ Automatic distribution across nodes
- ✅ No Redis dependency (use any Dapr state store)
- ✅ Type-safe interface
- ✅ Memory optimized (segment offloading reduces memory by 95%+)
- ❌ More overhead (actor framework)

**Redis Queue:**
- ✅ Lower overhead
- ✅ Battle-tested
- ❌ Manual distribution/sharding
- ❌ Requires Redis

### vs. Message Queue (RabbitMQ, Kafka)

**QueueActor:**
- ✅ Simpler setup
- ✅ Embedded in app (no separate broker)
- ❌ Not designed for high throughput
- ❌ Limited routing/filtering

**Message Queue:**
- ✅ High throughput
- ✅ Advanced routing
- ❌ Complex setup
- ❌ Separate infrastructure

### vs. Cloud Queues (SQS, Azure Queue)

**QueueActor:**
- ✅ Cloud-agnostic
- ✅ Run locally for dev
- ❌ Self-managed state store

**Cloud Queue:**
- ✅ Fully managed
- ✅ Proven scalability
- ❌ Cloud vendor lock-in
- ❌ Costs scale with usage

## Best Practices

1. **Use Descriptive Actor IDs**: `user-{userId}-tasks` not `queue-123`
2. **Leverage Segment Offloading**: Default configuration (buffer_segments=1) provides excellent memory savings for large queues
3. **Tune Buffer Segments**: Increase `buffer_segments` (2-5) for latency-sensitive applications
4. **Monitor State Store**: Watch state store size - all segments persist there even when unloaded from memory
5. **Dequeue Regularly**: While offloading handles large queues, regular consumption prevents unbounded growth
6. **Handle Empty Queue**: Dequeue returns empty list, not error
7. **Idempotent Consumers**: Operations may be retried on failure
8. **Use JsonSerializer**: Leverage System.Text.Json for consistent JSON serialization

## Limitations

- **Not a Full Message Broker**: No content-based routing or topic exchanges - fan-out is one topic to N subscriber queues (see [Topics](#topics-pubsub)); dead-letter queues are supported via `DeadLetter`
- **Segmented Storage**: Max 100 items per segment (hardcoded in MaxSegmentSize constant)
- **Memory Optimization**: With offloading enabled (v4.1+), only head, buffer, and tail segments kept in memory
- **Priority-Based Ordering**: Items are FIFO within each priority level (0 = highest priority)
- **No Transactions**: Enqueue/Dequeue are separate operations
- **State Store Dependency**: Requires configured Dapr state store
- **Language**: C# implementation only (.NET 10.0+)

## Further Reading

- [SESSIONS_IMPLEMENTATION.md](SESSIONS_IMPLEMENTATION.md) - Session-based ordered sub-queues (SessionCoordinatorActor, leasing, enforcement)
- [Dapr Actors Documentation](https://docs.dapr.io/developing-applications/building-blocks/actors/)
- [Virtual Actor Pattern (Orleans)](https://www.microsoft.com/en-us/research/project/orleans-virtual-actors/)
- [Actor Model (Wikipedia)](https://en.wikipedia.org/wiki/Actor_model)
