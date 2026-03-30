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
// queue_0_seg_0 (first segment, being popped from)
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

// queue_0_seg_2 (third segment, being pushed to)
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
- `head_segment`: Segment to pop from (oldest items)
- `tail_segment`: Segment to push to (newest items)
- `count`: Total items across all segments
- `head_offloaded_segment` (optional): First segment number in offloaded range (v4.1+)
- `tail_offloaded_segment` (optional): Last segment number in offloaded range (v4.1+)

### State Operations

**Push Operation:**
1. Validate items array (1-1000 items, non-empty ItemJson, priority >= 0 for each)
2. Group items by priority
3. For each priority group, load metadata and get tail segment number
4. For each item in group:
   - Load tail segment (e.g., `queue_0_seg_2`) from state store
   - If segment is full (100 items), allocate new segment (increment tail pointer)
   - Append item to tail segment
5. Update metadata (count, tail pointer) for each priority
6. Save all segments and metadata atomically with single SaveStateAsync()

**Pop Operation:**
1. Load metadata to determine which priorities have items
2. Sort priority keys numerically (0, 1, 2, ...)
3. For each priority in order, load head segment (e.g., `queue_0_seg_0`)
4. Pop single item from front of segment
5. If segment becomes empty:
   - If more segments exist: increment head pointer, don't save empty segment
   - If last segment: delete queue metadata
6. Save updated segment (if not empty) and metadata

**Benefits of Segmentation:**
- **Memory**: Load max 100 items per operation instead of entire queue
- **Network**: Serialize max 100 items instead of N items per save
- **Performance**: Pop becomes O(1) instead of O(N) for list slicing

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

**Offload Flow** (during Push):
1. After successful push, check if any segments are eligible
2. For each eligible segment:
   - Call `StateManager.UnloadStateAsync(segmentKey)` to unload from memory
   - Extend offloaded range (`head_offloaded_segment`/`tail_offloaded_segment`) in metadata
3. Continue (non-blocking on failure)

**Note**: Offloaded segments are always contiguous, so they're stored as a range (min/max) in metadata rather than a list, preventing unbounded metadata growth.

**Load Flow** (during Pop):
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
- **Frequency**: Load happens once per 100 pops when entering buffer zone
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
Request 1 → Push()  ──▶ [Processing...] ──▶ Response
Request 2 → Pop()   ──▶ [Queued...]     ──▶ [Processing...] ──▶ Response
Request 3 → Push()  ──▶ [Queued...]                ──▶ [Processing...] ──▶ Response
```

This eliminates race conditions - no locks needed!

### Multiple Actor Instances

Different actor IDs run in parallel:

```
Actor "queue-1" → Push()  ─┬─▶ [Processing independently]
Actor "queue-2" → Pop()   ─┼─▶ [Processing independently]
Actor "queue-3" → Push()  ─┴─▶ [Processing independently]
```

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

## Integration Patterns

### 1. Direct Actor Invocation

There are two ways to invoke actors directly: **remoting** (interface-based) and **nonremoting** (method name strings).

#### Remoting (Interface-Based)

```csharp
using Dapr.Actors.Client;
using DaprMQ.Interfaces;

var proxy = ActorProxy.Create<IQueueActor>(new ActorId("my-queue"), "QueueActor");
await proxy.Push(new PushRequest
{
    Items = new List<PushItem>
    {
        new PushItem { ItemJson = itemJson, Priority = 0 }
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
var result = await proxy.InvokeMethodAsync<PushRequest, PushResponse>(
    "Push",
    new PushRequest
    {
        Items = new List<PushItem>
        {
            new PushItem { ItemJson = itemJson, Priority = 0 }
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
curl -X POST http://localhost:8000/queue/queue-1/push
```

**Pros:**
- Language-agnostic
- Simple HTTP interface
- Easy to test with curl

**Cons:**
- HTTP overhead
- Requires API server

## Failure Handling

### State Store Failures

If state store is unavailable:
- Push/Pop operations fail and return error
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
5. **Pop Regularly**: While offloading handles large queues, regular consumption prevents unbounded growth
6. **Handle Empty Queue**: Pop returns empty list, not error
7. **Idempotent Consumers**: Operations may be retried on failure
8. **Use JsonSerializer**: Leverage System.Text.Json for consistent JSON serialization

## Limitations

- **Not a Message Broker**: No pub/sub, routing, or dead letter queues
- **Segmented Storage**: Max 100 items per segment (hardcoded in MaxSegmentSize constant)
- **Memory Optimization**: With offloading enabled (v4.1+), only head, buffer, and tail segments kept in memory
- **Priority-Based Ordering**: Items are FIFO within each priority level (0 = highest priority)
- **No Transactions**: Push/Pop are separate operations
- **State Store Dependency**: Requires configured Dapr state store
- **Language**: C# implementation only (.NET 10.0+)

## Further Reading

- [Dapr Actors Documentation](https://docs.dapr.io/developing-applications/building-blocks/actors/)
- [Virtual Actor Pattern (Orleans)](https://www.microsoft.com/en-us/research/project/orleans-virtual-actors/)
- [Actor Model (Wikipedia)](https://en.wikipedia.org/wiki/Actor_model)
