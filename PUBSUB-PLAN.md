# DaprMQ Pub/Sub — Canonical Implementation Plan

Status: planning complete, implementation not started.

## 1. Context

DaprMQ has no native pub/sub today. It has an *outbound* delivery mechanism only (`HttpSinkActor`, `DaprPubSubSinkActor` — reminder-driven pollers that call `PopWithAck` on a `QueueActor` and push items to an external HTTP endpoint or Dapr topic). That's DaprMQ acting as a publisher outward, not a fan-out primitive for multiple consumer groups.

**Chosen shape**: fan-out to consumer-group queues. A new `TopicActor` sits above `QueueActor`. Publishing to a topic makes the message available to every subscriber; each subscriber is backed by its own independent `QueueActor` instance, so every consumer group gets full FIFO/lock/DLQ semantics for free via delegation — no new queue-storage logic is written. Delivery is both **pull** (consumers call the existing Pop/PopWithAck/Acknowledge/ExtendLock API against their subscriber queue, unchanged) and **push** (a subscription can optionally register an HTTP or Dapr-pubsub sink on its provisioned queue, reusing the existing sink actors as-is).

Four requirements emerged during design and shape the architecture below:
1. **Eventually consistent, not best-effort fan-out** — a publish must not be silently dropped for any subscriber; relay must survive actor crashes and resume, not restart or lose work.
2. **Per-subscriber ordering** — if publish A precedes publish B, every subscriber must see A before B in its own queue.
3. **No head-of-line blocking across subscribers** — one slow/unreachable subscriber must not stall delivery to any other subscriber.
4. **Bounded operational cost** — no per-subscriber Dapr reminder (unbounded reminder count), no per-item subscriber-list copies (unbounded storage growth), no scan-based cleanup (unbounded reaper cost). Every recurring operation must be O(1) or O(subscriber count) per tick, never O(items) or O(items × subscribers).

Several earlier designs were considered and rejected during planning; each rejection is preserved in §2 because the reasoning constrains the final design and should not be re-litigated during implementation.

## 2. Architecture

### 2.1 Rejected alternatives (for context — do not resurrect during implementation)

- **Per-`PublishId` reminder, independently firing.** Breaks requirement 2 (ordering) — Dapr gives no cross-reminder ordering guarantee, so publish B's reminder could beat publish A's to a subscriber.
- **Single topic-wide reminder, head-of-queue-only processing.** Fixes ordering but violates requirement 3 — one slow subscriber stalls the whole topic's relay cursor.
- **`TopicActor → SubscriberActor → QueueActor` two-hop pipeline** (one middleman actor instance per subscriber). Considered to isolate a slow subscriber's retries onto a separate actor instance. Rejected: Dapr actors process one turn at a time per instance regardless of hop count, so this only partially shifts contention while doubling relay latency, doubling the state-machine testing surface, and adding a whole new actor type — not a clean win.
- **Per-published-item snapshot of the full subscriber-id list** (`TargetSubscriberIds` on every `item_{sequence}`). Rejected for unbounded storage growth: O(items × subscribers) string data over a topic's life. Replaced by the generation-pointer model in §2.3.
- **Per-subscriber outbox** (a full copy of every published item duplicated into each subscriber's own mailbox). Rejected for the same reason — N subscribers = N× payload storage per publish. Replaced by the shared item log in §2.2.
- **One Dapr reminder per subscriber** (`relay-{subscriberId}`). Rejected: unbounded reminder count competing for the same `TopicActor` instance's single turn-based execution queue — more subscribers means less turn-queue time for incoming `Publish`/`Subscribe` calls, with no actual concurrency gain since all reminders on one actor instance still serialize through that one queue. Replaced by the single `relay` reminder + in-process `Task.WhenAll` model in §2.4.
- **Reaper that scans the item log to determine if a generation snapshot is still referenced.** Rejected: unbounded scan cost. Replaced by the self-rescheduling, time-based `reap-generation-{id}` reminder in §2.3.
- **10s per-tick relay timeout.** Rejected as too permissive: since `Task.WhenAll` bounds tick cost by the timeout value regardless of subscriber count (100 subscribers timing out concurrently still costs one timeout period, not 100×), the timeout should reflect what a *single healthy in-cluster call* should cost (tens–low-hundreds of ms), not subscriber count. Final default: **2s** (`RelayTickTimeoutSeconds`).

### 2.2 Storage model — shared item log, per-subscriber cursor

Each published item is written **once**, keyed by a monotonic sequence number, in `TopicActor`'s own state. Subscribers do not own a copy of any item — each subscriber owns only a `long` cursor into the shared log (`DispatchCursors[subscriberId]`), marking the next sequence number that subscriber's relay still needs to deliver. This makes publish-time storage cost O(items), not O(items × subscribers), and makes "has this subscriber received item N" an O(1) cursor comparison rather than a per-subscriber existence check.

### 2.3 Subscriber-set membership — generation pointer, not a per-item list

Rather than stamping every item with the full list of subscribers it targets, `TopicActor` versions the subscriber set itself:
- `TopicMetadata.CurrentGeneration` (a `long`) increments by exactly one whenever `Subscribe`/`Unsubscribe` actually changes `SubscriberIds`.
- Each generation's subscriber-set snapshot is written **once**, immutably, under `subscriber-set-generation-{generationId}`, at the moment it's created.
- Every published item stamps only the current generation number (`item.Generation`, a single `long`), resolved to a concrete subscriber set by a lookup at relay time — cached per-tick so a run of items sharing a generation (the common case between subscription changes) costs one lookup, not one per item.
- **Cleanup is time-based, not scan-based.** When a generation snapshot is created, a reminder `reap-generation-{generationId}` is registered, deferred `GenerationRetentionDays` (default 7) into the future. On fire: if that generation is still `CurrentGeneration`, don't delete — just re-register for another `GenerationRetentionDays` out, indefinitely, for as long as it stays current. If it's been superseded, delete the snapshot and don't reschedule. This is O(1) per generation regardless of item-log size, at the cost of an accepted assumption: a generation is safe to delete once superseded *and* `GenerationRetentionDays` old, on the basis that no subscriber should realistically still be relaying items that stale — if one is, that's a starvation problem worth surfacing via `GetPublishStatus`/monitoring, not something cleanup should pay an unbounded scan to protect against.

### 2.4 Relay — single reminder, concurrent per-subscriber `Push`

One reminder per topic, name `relay`, on a tunable interval (`RelayIntervalSeconds`, dynamic-backoff pattern matching `DaprPubSubSinkActor.CurrentIntervalSeconds` — fast retry after a tick with new items, back off toward a ceiling when a tick finds nothing new). Each tick:

1. For every subscriber in `SubscriberIds` that is **not** currently in backoff (`circuit_{subscriberId}.NextRetryAt` still future) or blacklisted — skip those entirely, consuming no `Task` slot and no timeout budget.
2. For each eligible subscriber, build a batch: read up to `RelayBatchSize` items from the shared log starting at `DispatchCursors[subscriberId]`, resolving each item's `Generation` to a subscriber set (cached per-tick) and including the item only if this subscriber is a member; non-member items are still skipped past (cursor still advances over them).
3. Kick off one `QueueActor.Push` `Task` per eligible subscriber **concurrently**, each independently wrapped in a `RelayTickTimeoutSeconds` (default 2s) timeout, and `await Task.WhenAll(...)`.
4. Once every task has settled (succeeded / faulted / timed out): compute the new cursor per subscriber (advanced past its batch on success, unchanged on failure/timeout) and update each subscriber's circuit-breaker state (§2.5) — write **all** of it in one `SaveStateAsync()` call at the end of the tick. No per-subscriber write mid-tick.

This gives every subscriber independent ordering (its cursor only advances after its own successful `Push`, strictly by `Sequence`) and independent failure isolation (one subscriber's faulted/timed-out task cannot prevent another's `Task` in the same `WhenAll` from completing and having its cursor advance in that same tick) — while keeping the reminder count fixed at exactly one per topic. `QueueActor.Push` already accepts `List<PushItem>` and pushes in list order, so batching needs no `QueueActor` change.

### 2.5 Circuit breaker per subscriber

State key `circuit_{subscriberId}`, present only for a subscriber currently in or recovering from a failure streak (absent = healthy): `{ SubscriberId, ConsecutiveFailures, FirstFailureAt?, NextRetryAt?, Blacklisted }`.

- **On success**: delete `circuit_{subscriberId}` entirely (or reset `ConsecutiveFailures = 0`) — healthy subscribers carry no breaker state.
- **On failure/timeout**: increment `ConsecutiveFailures`; if first failure in a new streak, set `FirstFailureAt = now`; set `NextRetryAt = now + min(2^ConsecutiveFailures, cap)` seconds (exponential backoff) so subsequent ticks skip this subscriber until that time passes.
- **Blacklist**: if `now - FirstFailureAt >= 1 hour` for the current streak, set `Blacklisted = true`. From that point the relay tick permanently skips this subscriber — no more `NextRetryAt` scheduling, no more `Push` attempts — until manual intervention.
- **Manual reset (only way out of blacklist)**: `ResetCircuitBreaker(subscriberId)` deletes `circuit_{subscriberId}`. No data is lost while blacklisted — the cursor was frozen, not the underlying items (the shared log retains them; `reap-items`'s `Min()` over `DispatchCursors` already excludes a frozen cursor from being reaped past). The very next `relay` tick retries that subscriber from its unchanged cursor.
- **Status read**: `GetCircuitBreakerStatus(subscriberId)` — observability without inferring state from relay behavior.

### 2.6 Reaper (item log)

Separate reminder, name `reap-items`, fixed schedule (e.g. every 30s), independent of publish/relay activity. Each tick: `minCursor = min(DispatchCursors.Values)` (or `NextSequence` if zero subscribers); delete every `item_{sequence}` with `Sequence < minCursor`. Safe because an item below every subscriber's cursor has already been durably `Push`ed everywhere it needed to go. Unsubscribing removes that subscriber's cursor from the `Min()` computation, so it can't stall the reaper forever on a frozen cursor.

## 3. Files & Components

### 3.1 New files

- `dotnet/src/DaprMQ.Interfaces/ITopicActor.cs` — new actor interface (§3.3).
- `dotnet/src/DaprMQ/TopicActor.cs` — new actor implementation (state + reminders: `relay`, `reap-items`, `reap-generation-{id}`; `IRemindable`).
- `dotnet/src/DaprMQ.ApiServer/Controllers/TopicController.cs` — new HTTP controller, route base `topic`.
- `dashboard/src/hooks/useTopicOperations.ts` — new dashboard hook, mirrors `useQueueOperations.ts` shape (`topicApi` REST client + one hook exposing publish/subscribe/unsubscribe/listSubscribers/resetCircuitBreaker, `isLoading`/`error` pairs).
- Tests (see §7 for full breakdown): `TopicActorTests.cs`, `TopicActorReaperTests.cs`, `TopicActorCircuitBreakerTests.cs`, `TopicControllerTests.cs`, `dotnet/tests/DaprMQ.IntegrationTests/Tests/TopicTests.cs`.

### 3.2 Modified files

- `dotnet/src/DaprMQ.Interfaces/Models.cs` — add all new DTOs (§3.4).
- `dotnet/src/DaprMQ.ApiServer/Constants/ActorMethodNames.cs` — add: `Publish`, `Subscribe`, `Unsubscribe`, `ListSubscribers`, `GetPublishStatus`, `ResetCircuitBreaker`, `GetCircuitBreakerStatus`.
- `dotnet/src/DaprMQ.ApiServer/Program.cs` — register `TopicActor` (`options.Actors.RegisterActor<DaprMQ.TopicActor>(actorConfig.TopicActorTypeName)`, config key `TOPIC_ACTOR_TYPE_NAME` default `"TopicActor"`), register `ITopicActorInvoker` DI binding (mirrors existing `IQueueActorInvoker` binding at `Program.cs:98-119`).
- `dotnet/src/DaprMQ.Interfaces/ITopicActorInvoker.cs` (or co-located with other invoker interfaces) — new marker interface `: IActorInvoker`; concrete impl added alongside existing invokers in `DaprActorInvoker.cs`.
- `docs/ARCHITECTURE.md` — remove/update the stale "Limitations: no pub/sub" line (dead-letter already contradicts it); add a "Topics" section describing the fan-out model.
- `docs/API_REFERENCE.md` — document all new `/topic/*` endpoints, the async publish/accept contract, error codes and HTTP mappings.

### 3.3 `ITopicActor` interface

```csharp
public interface ITopicActor : IActor, IRemindable
{
    Task<PublishResponse> Publish(PublishRequest request);
    Task<SubscribeResponse> Subscribe(SubscribeRequest request);
    Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request);
    Task<ListSubscribersResponse> ListSubscribers();
    Task<PublishStatusResponse> GetPublishStatus(GetPublishStatusRequest request);
    Task<ResetCircuitBreakerResponse> ResetCircuitBreaker(ResetCircuitBreakerRequest request);
    Task<CircuitBreakerStatusResponse> GetCircuitBreakerStatus(GetCircuitBreakerStatusRequest request);
}
```

No `SubscriberActor` / `EnqueueBatch` — that design was rejected (§2.1). `QueueActor` is unmodified; `TopicActor` calls its existing `Push` via `IQueueActorInvoker`.

### 3.4 New DTOs (`Models.cs`)

```csharp
public record PublishRequest { public List<PushItem> Items { get; init; } = new(); }
public record PublishResponse { public bool Accepted { get; init; } public required string PublishId { get; init; } public required long Sequence { get; init; } public string? ErrorMessage { get; init; } }

public record SubscribeRequest { public required string SubscriberId { get; init; } public SinkConfig? Sink { get; init; } }
public record SubscribeResponse { public bool Success { get; init; } public required string QueueActorId { get; init; } public string? ErrorCode { get; init; } public string? ErrorMessage { get; init; } }

public record UnsubscribeRequest { public required string SubscriberId { get; init; } }
public record UnsubscribeResponse { public bool Success { get; init; } public string? ErrorCode { get; init; } public string? ErrorMessage { get; init; } }

public record ListSubscribersResponse { public List<string> SubscriberIds { get; init; } = new(); }

public record GetPublishStatusRequest { public required string PublishId { get; init; } }
public record PublishStatusResponse { public bool Found { get; init; } public bool Complete { get; init; } public List<string> TargetSubscriberIds { get; init; } = new(); public List<string> DeliveredSubscriberIds { get; init; } = new(); }

public record ResetCircuitBreakerRequest { public required string SubscriberId { get; init; } }
public record ResetCircuitBreakerResponse { public bool Success { get; init; } public string? ErrorCode { get; init; } public string? ErrorMessage { get; init; } }

public record GetCircuitBreakerStatusRequest { public required string SubscriberId { get; init; } }
public record CircuitBreakerStatusResponse { public bool Found { get; init; } public int ConsecutiveFailures { get; init; } public double? FirstFailureAt { get; init; } public double? NextRetryAt { get; init; } public bool Blacklisted { get; init; } }
```

### 3.5 `TopicActor` internal state

| State key | Type | Notes |
|---|---|---|
| `metadata` | `TopicMetadata { List<string> SubscriberIds; long NextSequence; long CurrentGeneration; Dictionary<string, long> DispatchCursors }` | `NextSequence`/`CurrentGeneration` are monotonic counters. |
| `subscriber-set-generation-{generationId}` | `SubscriberSetGeneration { long GenerationId; List<string> SubscriberIds }` | Written once, immutable, one per generation. |
| `item_{sequence}` | `TopicItem { long Sequence; PushItem Item; long Generation }` | One per published item; reaped once every cursor has passed it. |
| `circuit_{subscriberId}` | `CircuitBreakerState { string SubscriberId; int ConsecutiveFailures; double? FirstFailureAt; double? NextRetryAt; bool Blacklisted }` | Present only during/after a failure streak. |

### 3.6 HTTP routes (`TopicController`, route base `topic`)

- `POST /topic/{topicId}/publish` — 202 Accepted, body `{ PublishId, Sequence }` (async, not a delivery receipt).
- `POST /topic/{topicId}/subscribers/{subscriberId}` — register subscriber, optional sink config; 201/200, 409 `SUBSCRIBER_EXISTS`.
- `DELETE /topic/{topicId}/subscribers/{subscriberId}` — unsubscribe; 200, 404 `SUBSCRIBER_NOT_FOUND`.
- `GET /topic/{topicId}/subscribers` — list.
- `GET /topic/{topicId}/publish/{publishId}` — relay status (target vs. delivered subscriber ids).
- `POST /topic/{topicId}/subscribers/{subscriberId}/reset-circuit-breaker` — 200, 404 if subscriber missing.
- `GET /topic/{topicId}/subscribers/{subscriberId}/circuit-breaker` — read-only breaker status.

**gRPC**: deferred. `DaprMQGrpcService.cs` mirrors only the original queue primitives; sinks never got gRPC parity either. Topic follows the same precedent until a concrete gRPC consumer exists.

## 4. Invariants & Constraints

1. **A subscriber's cursor advances only after a successful, durable `Push` to its `QueueActor`.** Never advance speculatively; a failed/timed-out `Push` leaves the cursor untouched for retry on a later tick.
2. **All cursor + circuit-breaker mutations for a `relay` tick are written in exactly one `SaveStateAsync()` call**, after every concurrent `Push` task for that tick has settled. No partial/interleaved state write mid-tick.
3. **A subscriber's own relay outcome must never depend on another subscriber's outcome in the same tick.** One subscriber faulting/timing out must not prevent any other subscriber's task in the same `Task.WhenAll` from completing and advancing its own cursor in that tick.
4. **Reminder count is fixed at exactly one `relay` reminder and one `reap-items` reminder per topic**, regardless of subscriber count. Per-generation `reap-generation-{id}` reminders are the only count that scales — with generations (subscription changes), not with items or subscribers per tick.
5. **No recurring operation scans the item log by content.** `reap-items` only compares `Sequence` against `DispatchCursors.Min()`; `reap-generation-{id}` only compares against `CurrentGeneration` and elapsed time. Neither iterates unbounded item state.
6. **A new subscriber's cursor is initialized to `NextSequence` at subscribe time**, never to `0` — it must never receive items published before it existed.
7. **Generation snapshots are immutable once created.** `Subscribe`/`Unsubscribe` only ever creates a new generation; it never rewrites an existing `subscriber-set-generation-{id}`. This is what lets already-published items keep pointing at a stable, historically-accurate membership set.
8. **`Unsubscribe` does not retroactively affect already-published items.** An item published before an unsubscribe still targets the generation (and thus the subscriber set) active at publish time — including a subscriber that later unsubscribed. Only items published after the new generation exists exclude that subscriber.
9. **`Unsubscribe` does not delete the underlying `QueueActor`'s state.** Matches existing sink-unregister behavior (`UnregisterSink` never deletes the queue). No "delete queue state" capability is introduced.
10. **Blacklisting is manual-reset-only.** No automated process ever clears `Blacklisted = true`; only `ResetCircuitBreaker` can.
11. **Controller validates request format (empty ids, malformed sink config); actor validates business logic (subscriber existence, generation consistency)** — matches the existing `QueueController`/`QueueActor` split mandated by CLAUDE.md.
12. **`ActorMethodNames` constants are used for every non-remoting actor invocation — never raw strings** (existing repo-wide convention, CLAUDE.md).
13. **Segmented-storage / single-`SaveStateAsync()`-per-operation convention applies to `TopicActor` the same as `QueueActor`** — batch state changes, one save per logical operation (`Publish`, one `relay` tick, one `reap-items` tick, etc.), not one save per item/subscriber.

## 5. Edge Cases

- **Zero subscribers.** `Publish` still writes items and bumps `NextSequence`; `reap-items` uses `NextSequence` (not `DispatchCursors.Min()`, which would be undefined) and reaps everything immediately — items with no subscriber to deliver to shouldn't accumulate.
- **Subscribe after items already exist.** New subscriber's cursor starts at current `NextSequence` (invariant 6) — never receives backlog. This is deliberate: a topic is a live fan-out, not a replay log.
- **Unsubscribe while relay is mid-tick for that subscriber.** The in-flight `Push` task for this tick may already have been dispatched before the generation changed; let it complete normally (invariant 8 — that item was validly targeted at publish time). The *next* tick simply no longer includes this subscriber (removed from `SubscriberIds`) and no longer computes its cursor.
- **Unsubscribe then re-Subscribe with the same `subscriberId`.** Treated as a brand-new subscription: new `DispatchCursors` entry initialized to current `NextSequence`, new generation created including it. The previously-provisioned `QueueActor` for `{topicId}-sub-{subscriberId}` still exists with whatever state was left in it (per invariant 9) — re-subscribing does not clear or reset that queue. Flagged as a decision, not fully explored: if a caller wants a clean queue on re-subscribe, that's out of scope for this plan (see §9).
- **A subscriber's `Push` batch is entirely non-member items** (all skipped due to generation mismatch). Cursor still advances past them; no actual `Push` call is needed for a fully-empty batch — skip issuing the task at all for that subscriber this tick.
- **Reminder firing after actor deactivation/reactivation.** `OnActivateAsync` re-registers `relay` if any `DispatchCursors` entry is behind `NextSequence`, `reap-items` unconditionally, and `reap-generation-{CurrentGeneration}` if not already scheduled. Superseded (non-current) generations are not proactively re-registered on activation even if lost — worst case is a slow leak of one orphaned snapshot, judged acceptable versus adding scan-based detection.
- **Circuit breaker: success immediately after a long failure streak but before blacklist.** A single success fully clears `circuit_{subscriberId}` (deletes it), not a partial decrement — no lingering "almost blacklisted" state carries into the next streak.
- **Circuit breaker: `ResetCircuitBreaker` called on a healthy subscriber (no existing `circuit_{subscriberId}`).** No-op success, not an error (invariant matches error-handling policy in §6/§7).
- **Multi-item `Publish` call.** Each item in the request gets its own `Sequence` (strictly increasing within the call), and all items in one `Publish` call share the same `Generation` snapshot and are written via one `SaveStateAsync()`.
- **`GetPublishStatus` for a `PublishId` whose items have already been reaped from the log.** `Found = false` is acceptable — publish status is an observability aid for in-flight relay, not a permanent audit log. This is a deliberate scope limit (see §9 if durability of publish history becomes a requirement).
- **Very large subscriber count in one `relay` tick (e.g. 100+).** Bounded by `RelayTickTimeoutSeconds` (2s) per subscriber task, run concurrently — wall-clock cost for the tick is ~2s worst case regardless of count, not 2s × count. `Task.WhenAll` must be used, not sequential `await` in a loop, or this bound doesn't hold — this is a correctness requirement for the implementation, not just a performance nicety.

## 6. Implementation Sequence

Outside-in per CLAUDE.md's mandatory TDD workflow (integration test → controller → actor, each step's test written *before* its implementation). Recommended order, each step gated on the previous step's tests passing:

1. **Scaffolding**: `ITopicActor.cs`, DTOs in `Models.cs`, `ActorMethodNames` additions, `ITopicActorInvoker` + impl, `Program.cs` registration. No behavior yet — this just makes the surface compile.
2. **`Subscribe` / `Unsubscribe` / `ListSubscribers`** (no relay yet): subscriber-set + generation bookkeeping, cursor initialization, sink wiring passthrough to existing `InitializeHttpSink`/`InitializeDaprPubSubSink`. Test: subscribe/unsubscribe/list round-trip, generation increments correctly, cursor initialized to current `NextSequence`.
3. **`Publish`** (write-only, no relay yet): item log writes, sequence/generation stamping, single `SaveStateAsync()`. Test: published items land in the log with correct `Sequence`/`Generation`; `Publish` returns immediately without touching cursors.
4. **`relay` reminder — single subscriber, no concurrency/circuit-breaker yet**: prove the basic read-cursor → resolve-generation → `Push` → advance-cursor loop works end to end for one subscriber.
5. **`relay` reminder — concurrency**: extend to `Task.WhenAll` over multiple subscribers, single end-of-tick `SaveStateAsync()`, per-task timeout. Test: no-head-of-line-blocking (one subscriber fails, others still advance in the same tick).
6. **Circuit breaker**: backoff/blacklist state machine layered into the same tick, `ResetCircuitBreaker`/`GetCircuitBreakerStatus`.
7. **`reap-items` reminder**: `Min()`-cursor-based item deletion.
8. **`reap-generation-{id}` reminder**: self-rescheduling generation cleanup.
9. **`GetPublishStatus`**: read-only resolution of target vs. delivered subscriber ids for a given `PublishId`.
10. **`TopicController`**: HTTP routes wrapping the above, validation, error-code → HTTP-status mapping.
11. **Dashboard hook** (`useTopicOperations.ts`).
12. **Docs** (`ARCHITECTURE.md`, `API_REFERENCE.md`).
13. **Integration test** (`TopicTests.cs`) — written first per outside-in TDD, but listed last here only because it's the thing that exercises steps 1–10 together end to end; in practice write it before step 2 and let it fail until the pieces exist, per the mandatory workflow.

Run `dotnet test` in `dotnet/tests/DaprMQ.Tests/` after every step. Run `./build-and-test.sh` before any commit.

## 7. Testing Strategy (TDD, per CLAUDE.md)

Mandatory sequence: integration test (HTTP contract) → controller test (mocked actor) → actor test (mocked state manager) → `dotnet test` → `./build-and-test.sh` before commit.

### 7.1 Integration (`dotnet/tests/DaprMQ.IntegrationTests/Tests/TopicTests.cs`)

- Subscribe two subscribers → publish → poll `GET /topic/{id}/publish/{publishId}` until `Complete=true` → pop from each subscriber's queue, confirm both received the item.
- Publish a second message before the first finishes draining → once both drained, assert each subscriber's queue has both messages **in publish order** (ordering test).
- Unsubscribe one → publish again → confirm only the remaining subscriber's cursor targets it (removed one no longer in `DispatchCursors`).

### 7.2 Controller (`TopicControllerTests.cs`, mocked `ITopicActorInvoker`)

- `Publish` → 202 + `PublishId` on success, 400 on empty items.
- `Subscribe` → 201/200 + queue id; 409 on actor-reported `SUBSCRIBER_EXISTS`.
- `Unsubscribe` → 200; 404 on `SUBSCRIBER_NOT_FOUND`.
- `GetPublishStatus` → 404 when actor reports not-found.
- `ResetCircuitBreaker` → 200 on success; 404 on `SUBSCRIBER_NOT_FOUND`.
- `GetCircuitBreakerStatus` → 200 with breaker fields when found; 404 when not.

### 7.3 Actor — core (`TopicActorTests.cs`, mocked `IActorStateManager` + `IQueueActorInvoker` + reminder registration)

- `Subscribe`: adds to `SubscriberIds`, bumps `CurrentGeneration`, writes the new generation snapshot, initializes `DispatchCursors[subscriberId] = NextSequence`, one `SaveStateAsync()`.
- `Publish`: strictly-increasing `Sequence`, one `item_{sequence}` per item stamped with `CurrentGeneration` (never a copied list), does not touch any cursor, ensures `relay` is registered.
- `relay` tick, two subscribers: `Push` calls issued concurrently (assert via mock timing/`Task.WhenAll` semantics, not sequential await); generation resolution excludes an item from a subscriber whose generation predates it; one subscriber's `Push` set to fail, the other to succeed — assert the successful one's cursor advances and the failing one's doesn't, **both written in the same `SaveStateAsync()` call**; a `Push` that never completes within `RelayTickTimeoutSeconds` is treated as failed, not awaited indefinitely.
- Backfill exclusion: subscriber added after items exist gets a cursor past them, never receives them.
- `Unsubscribe`: removes from `SubscriberIds`/`DispatchCursors`, writes a new generation snapshot excluding it; publishing before/after an unsubscribe produces items on two different generations, only the earlier one's resolved set includes the removed subscriber.

### 7.4 Actor — reaper (`TopicActorReaperTests.cs`, mocked `IActorStateManager`)

- `reap-items`: `Min()` over `DispatchCursors` bounds deletion correctly; zero subscribers reaps everything up to `NextSequence`; an item still needed by a lagging cursor is never deleted.
- `reap-generation-{id}`: fired while still `CurrentGeneration` → no delete, reschedules; fired after superseded → deletes, does not reschedule; never inspects `item_{sequence}` state (confirms time/generation-driven, not scan-based).

### 7.5 Actor — circuit breaker (`TopicActorCircuitBreakerTests.cs`, mocked state manager + invoker + controllable fake clock)

- Single failure creates `circuit_{subscriberId}` with `ConsecutiveFailures = 1` and a future `NextRetryAt`; a tick before `NextRetryAt` skips that subscriber (`Push` mock never invoked).
- Consecutive failures increase backoff delay each time (exponential growth, compare successive gaps).
- Simulated 1-hour elapsed time since `FirstFailureAt` → `Blacklisted = true`; no further attempts or scheduling regardless of further elapsed time.
- A single success fully clears `circuit_{subscriberId}`.
- `ResetCircuitBreaker` on blacklisted → deletes state, next tick retries from unchanged cursor.
- `ResetCircuitBreaker` on already-healthy → no-op success.
- One subscriber's backoff/blacklist state has zero effect on other subscribers' ticks/cursors (isolation).

Run `dotnet test` in `dotnet/tests/DaprMQ.Tests/` after each of 7.2–7.5; all green before moving to the next. `./build-and-test.sh` before any commit.

## 8. Acceptance Criteria

- [ ] A message published to a topic is delivered to every subscriber's queue, exactly once per subscriber per publish, without requiring the publisher to know how many subscribers exist.
- [ ] Delivery is eventually consistent: `Publish` returns immediately (202) and relay completes asynchronously, surviving an actor crash mid-relay by resuming from persisted cursor state rather than restarting or dropping items.
- [ ] Per-subscriber ordering holds under concurrent publishes: two publishes made back-to-back are delivered to every subscriber in the same relative order they were published.
- [ ] One subscriber with a permanently failing/unreachable `QueueActor.Push` does not delay or block delivery to any other subscriber, in any single `relay` tick or across ticks.
- [ ] Topic-level reminder count is exactly 2 fixed reminders (`relay`, `reap-items`) plus O(generations-created) `reap-generation-{id}` reminders — never O(subscribers) or O(items).
- [ ] No per-publish storage cost scales with subscriber count (shared item log + generation pointer, not per-subscriber copies).
- [ ] A subscriber that fails continuously for 1 hour is blacklisted and stops consuming relay resources; it resumes only via explicit `ResetCircuitBreaker`, never automatically.
- [ ] Both pull (existing Pop/PopWithAck/Acknowledge/ExtendLock against the subscriber's queue) and push (existing HTTP/Dapr-pubsub sink registered on the subscriber's queue) delivery models work against a subscription's provisioned queue, unmodified from their current queue-level behavior.
- [ ] All new code follows CLAUDE.md conventions: `ActorMethodNames` constants (no raw strings), segmented/batched state writes, controller validates format / actor validates business logic, documented error-code → HTTP-status mapping.
- [ ] `dotnet test` passes for all new/existing tests; `./build-and-test.sh` passes before any commit.
- [ ] `docs/ARCHITECTURE.md` and `docs/API_REFERENCE.md` reflect the new Topic capability and no longer claim pub/sub doesn't exist.

## 9. Unresolved / Open Questions

These were not settled during planning and should be decided before or during implementation of the relevant piece — none of them block starting §6 step 1, but each blocks the step noted:

1. **Re-subscribe after unsubscribe with the same `subscriberId` (§5)** — should the previously-provisioned `QueueActor` be left as-is (current plan default, invariant 9), or should there be an explicit way to get a clean queue on re-subscribe? Blocks: nothing immediately; revisit if it comes up in step 2 testing.
2. **`GetPublishStatus` durability window (§5)** — status becomes unavailable once an item is reaped from the log (days, per `GenerationRetentionDays`-adjacent but not identical lifetime — the item log's own retention is governed by subscriber cursors, not by the generation retention window). Is "status disappears once fully delivered and reaped" acceptable permanently, or does this need a longer-lived delivery-history record? Blocks: step 9 (`GetPublishStatus`) — implement the simple version now, treat this as a follow-up if raised.
3. **`RelayBatchSize`, `RelayIntervalSeconds`, `RelayTickTimeoutSeconds` (2s default), `GenerationRetentionDays` (7-day default), and the exponential-backoff cap/base for the circuit breaker** — defaults are proposed throughout this plan but not load-tested. Blocks: nothing structurally, but these should be exposed as configurable (topic-level config or `MetadataConfig`-equivalent) from the start so they don't require a schema change later to tune.
4. **Config surface for the above** — is topic-level config passed at `Subscribe`/topic-creation time, or is it a global `TopicActor` default with no per-topic override? Not decided. Blocks: step 2/3 (need the shape of `MetadataConfig`-equivalent before writing `Publish`/`relay`).
5. **A real `ErrorCode` enum** — flagged during planning as a good forcing function (the existing codebase uses informal string error codes throughout, not a real enum despite CLAUDE.md describing one). Explicitly **not** in scope for this feature; recommended as a separate follow-up, not a blocker.
6. **gRPC parity** — explicitly deferred (§3.6), consistent with existing sink actors never getting gRPC parity. Revisit only if a concrete gRPC consumer emerges.
7. **Whether `Push` failures inside a `relay` tick should distinguish "transient" vs. "permanent" failure types** for circuit-breaker purposes — the current plan treats every failure/timeout identically for backoff purposes. Whether some `QueueActor` error responses should skip straight to a faster backoff (or bypass the breaker, e.g. a validation error that will never succeed on retry) was not discussed. Blocks: step 6 (circuit breaker) — implement the uniform-treatment version first; revisit if `QueueActor.Push` error taxonomy is examined more closely.
