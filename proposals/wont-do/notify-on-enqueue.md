# Notify-on-enqueue for Consume streams

**Status:** rejected. See [ADR 0003](../../docs/ADR/0003-do-not-adopt-notify-on-enqueue.md). Kept as a record of the
design. Follows up the "notify-on-enqueue" item in
[consume-stream-sdks.md](../consume-stream-sdks.md#follow-ons),
[continuous-receive.md](../continuous-receive.md) and
[ADR 0001](../../docs/ADR/0001-consume-session-ack-driven-prefetch-refill.md#not-addressed).

## Context

`Consume` and `ConsumeSession` streams live on a gateway (`REGISTER_ACTORS=false`, app-id `daprmq-gateway`). The queue
actors they read live on workers (app-id `daprmq-service`). A stream finds new messages only by polling the actor with
`DequeueLocked`:

- `Consume` waits 200 ms after an empty dequeue, doubling up to `MaxIdlePollInterval` (2 s), and resets on a delivery
  ([DaprMQGrpcService.cs:1302-1310](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L1302-L1310)).
- `ConsumeSession` waits a flat `PollInterval` (200 ms) with no backoff
  ([DaprMQGrpcService.cs:1050](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L1050)).

The measured costs, from [consume-stream-sdks.md](../consume-stream-sdks.md):

| | Today |
| --- | --- |
| Idle-to-first-message latency (`Consume`, fully backed off) | ≈1.05 s p50, up to 2 s |
| Idle-to-first-message latency (`live-publish`, sessions) | ≈114 ms p50 |
| 200 idle `Consume` streams (with backoff) | ≈⅛ of a core across gateway, workers and Postgres |

The poll interval trades one cost against the other: a shorter poll lowers latency and raises CPU. AMQP brokers avoid
the trade-off because the broker pushes a message onto any link that has credit as soon as the message arrives. This
proposal gives DaprMQ the same property without changing the client protocol.

### Constraints

1. **No network calls inside an actor turn.** A turn holds the queue's lock, so every enqueue would wait for the call
   to finish. The worker already has a pattern for this: the actor hands an in-process signal to a worker singleton,
   which does the network I/O away from the turn
   ([SessionActivityReporter.cs](../../server/src/DaprMQ.ApiServer/Services/SessionActivityReporter.cs), called from
   `AfterActorMethodAsync`,
   [QueueActor.cs:473](../../server/src/DaprMQ/QueueActor.cs#L473)).
2. **No actor long-poll.** A `WaitForItems` actor method would hold the queue's turn for as long as it waited.
   Reentrancy doesn't help: it only lets a call chain re-enter itself, and separate chains are still serialised
   ([ADR 0002](../../docs/ADR/0002-enable-actor-reentrancy.md#consequences)).
3. **Dapr can't address one gateway instance.** Service invocation targets an app-id and load-balances across its
   replicas (3 by default in [values.yaml](../../helm/values.yaml)), so a worker can't use it to reach the gateway that
   holds a particular stream.
4. **No new infrastructure.** DaprMQ needs only a state store today. A pub/sub broker just to deliver wake-up hints is
   too large a dependency.

## Design principle: a wake is a hint

A wake means only "dequeue now instead of waiting". It never carries a message, and correctness never depends on one
arriving. Every stream keeps its fallback poll, so a lost, late or duplicate wake costs latency or one extra empty
`DequeueLocked`, never a lost or duplicated message. This keeps the design free of acks, retries and persistent
subscription state.

## Proposal

The work ships in two phases. Phase 1 needs no cross-process changes and fully covers single-gateway deployments.
Phase 2 covers every enqueue path in any topology.

### Phase 1: gateway-local wakes

A gateway singleton, `QueueWakeRegistry`, maps an actor id to the streams on this gateway waiting on it:

```csharp
internal sealed class QueueWakeRegistry
{
    // Registers a waiting stream; dispose to unregister. The returned signal latches, so a wake that
    // lands before the stream starts waiting is not lost.
    public WakeSubscription Subscribe(string actorId);

    // Wakes up to `available` of the streams waiting on actorId, oldest waiter first.
    public void Wake(string actorId, int available = int.MaxValue);
}
```

- `Consume` and `ConsumeSession` subscribe while their queue is empty. They replace `Task.Delay(wait)` with
  `Task.WhenAny(subscription.Signal.WaitAsync(), Task.Delay(wait))`.
- After a successful enqueue, the REST and gRPC `Enqueue` handlers call `Wake(queueId, itemsEnqueued)`.
- After a successful `Nack`, the `Consume` settler calls `Wake(queueId, 1)`, so a nacked message is picked up straight
  away by another stream on this gateway.
- `available` caps how many streams wake, which bounds the thundering herd: one enqueued message wakes one stream, not
  every stream on the queue.

**What phase 1 covers.** Every enqueue that passes through the same gateway as the consumer. With one gateway, as in
docker-compose, that is every client enqueue.

**What phase 1 misses:**
- With N gateways, the enqueues that arrive at the other N−1.
- Writes that start inside a worker: `TopicActor` fan-out to subscriber queues, nacks from other gateways, items
  restored by lock expiry, HTTP sink returns, and dead-letter moves.

The fallback poll still covers all of these.

### Phase 2: actor-side waiters, pushed directly to the gateway

```
 gateway-2                          worker-1 (hosts queue "orders")
 ┌───────────────────────┐          ┌──────────────────────────────────────┐
 │ Consume stream        │ 1 DequeueLocked{WakeTarget=gw-2}  QueueActor    │
 │   empty → subscribe ──┼─────────►│  empty → waiters[gw-2] = expiry      │
 │                       │          │                                      │
 │                       │          │  2 Enqueue (any path) → after save:  │
 │                       │          │    waiters non-empty & has items     │
 │                       │          │    → notifier.Notify(gw-2, ...)      │
 │                       │          │      (in-process, no await)          │
 │ QueueWakeRegistry     │ 3 Wake   │  QueueWakeNotifier (background)      │
 │   .Wake("orders", n) ◄┼──────────┤    coalesces per gateway, sends      │
 │ stream dequeues now   │          │    one Wake RPC                      │
 └───────────────────────┘          └──────────────────────────────────────┘
```

#### 1. Registration piggybacks on the empty dequeue

`DequeueLockedRequest` ([Models.cs:217](../../server/src/DaprMQ.Interfaces/Models.cs#L217)) gains:

```csharp
/// Gateway endpoint to notify when this queue next has claimable items. Set only by Consume/ConsumeSession.
public string? WakeTarget { get; init; }
```

`DequeueLockedResponse` ([Models.cs:354](../../server/src/DaprMQ.Interfaces/Models.cs#L354)) gains:

```csharp
/// True when the actor recorded WakeTarget as a waiter. False from a worker without this feature.
public bool WakeRegistered { get; init; }

/// Earliest expiry among this queue's outstanding locks (Unix seconds), when it returned no items but holds locks.
public double? NextLockExpiresAt { get; init; }
```

When `DequeueLocked` returns no items (empty, `Locked`, or `MaxConcurrencyReached`) and `WakeTarget` is set, the actor
records the waiter **in the same turn**. This rules out a race between "found empty" and "registered": any later
enqueue is a later turn and sees the waiter.

Waiters live in a field on the actor instance: `Dictionary<string, DateTimeOffset> _wakeWaiters`, mapping endpoint to
expiry. They are **not actor state**: no state keys and no extra `SaveStateAsync`. Losing them on deactivation is
acceptable (see Failure modes). Each entry expires `WakeRegistrationTtl` (default 60 s) after it was registered. The
gateway's next fallback poll renews it.

The table holds one entry per gateway, not per stream. 50 streams from one gateway on one queue are a single waiter,
and the gateway's registry fans the wake out locally.

#### 2. Deciding when to notify

In `AfterActorMethodAsync`, after any method completes and its state is saved, the actor checks:

```
waiters non-empty
  AND claimable > 0, where claimable = RemainingItems(metadata)
      (only once LockCount == 0 when the waiter is in exclusive mode, AllowCompetingConsumers=false)
```

If the check passes, the actor calls `_wakeNotifier.Notify(endpoint, actorId, claimable)` for each unexpired waiter,
then clears the waiters. The clear makes each waiter one-shot, like a long-poll. The stream re-registers on its next
empty dequeue.

This one check covers every way a queue gains claimable items, because they all end in an actor method on that queue:
`Enqueue` from a gateway, `Enqueue` from `TopicActor` fan-out, `Nack`, `Acknowledge`/`AcknowledgeBatch` dropping
`LockCount` to 0 in exclusive mode, the lock-expiry sweep on activation, and lease release on a session actor.

**Lock expiry is lazy, so it creates no event.** Locks are swept only at the top of
`Dequeue`/`DequeueLocked`/`SetSessionLease` and on activation
([QueueActor.cs:625](../../server/src/DaprMQ/QueueActor.cs#L625)). No method runs at the moment a lock expires, so no
wake can fire then. `NextLockExpiresAt` covers this: the gateway caps its wait at that time, so its next poll runs the
sweep that brings the item back. The actor reads it from the earliest non-empty `locks_exp_{bucket}`, so an
approximation at bucket granularity is fine.

#### 3. The worker-side notifier

`QueueWakeNotifier` is a worker `BackgroundService`, in the same shape as `SessionActivityReporter`:

- `Notify` is synchronous, takes a lock and never blocks the turn. It adds `(actorId, available)` to a pending set for
  each endpoint, keeping the maximum `available` per actor.
- A send loop per endpoint sends everything pending as one `Wake` RPC. Notifications that arrive while a send is in
  flight go out together in the next call, using the same coalescing as the `Consume` settler's batched acks.
- A failed send is logged and dropped. It is a hint, and the fallback poll covers it.
- When the target is this process (a combined deployment where workers also run the API with `ENABLE_API=true`), it
  calls the local `QueueWakeRegistry` directly.

#### 4. The gateway's wake endpoint

```proto
// Internal: worker -> gateway. Not exposed through the public Service or ingress.
service DaprMQInternal {
  rpc Wake(WakeRequest) returns (WakeResponse);
}
message WakeRequest  { repeated QueueWake queues = 1; }
message QueueWake    { string actor_id = 1; int32 available = 2; }
message WakeResponse {}
```

- The gateway serves it on a separate port (proposed **8083**) that is left out of the gateway `Service` and the
  ingress.
- The gateway learns its own address from `GATEWAY_WAKE_ENDPOINT`. In Helm that is
  `http://$(POD_IP):8083`, with `POD_IP` from the downward API. In docker-compose it is the container name, for
  example `http://gateway-1:8083`.
- When `GATEWAY_WAKE_ENDPOINT` is unset, the gateway sends no `WakeTarget` and behaves as it does today.

**Security.** This call goes pod to pod, not through Dapr, so it doesn't get Dapr's mTLS. It carries no message data,
only actor ids and counts, and the worst a forged call can do is cause extra empty dequeues. Even so:
- The Helm chart adds a NetworkPolicy that allows 8083 only from worker pods.
- An optional shared token (`DAPRMQ_INTERNAL_TOKEN`) can be checked in a request header.

#### 5. Gateway stream loop changes

`Consume` and `ConsumeSession` change only in how they wait after an empty dequeue:

```csharp
var request = new DequeueLockedRequest { ..., WakeTarget = _wakeEndpoint };
...
if (result.Items.Count == 0)
{
    using var sub = _wakeRegistry.Subscribe(actorId.GetId());
    var cap = result.WakeRegistered ? MaxIdlePollIntervalWithWake : MaxIdlePollInterval;
    var wait = Min(idleWait, untilRenewal, untilNextLockExpiry(result.NextLockExpiresAt));
    await Task.WhenAny(sub.Signal.WaitAsync(cts.Token), Task.Delay(wait, cts.Token));
    idleWait = Min(idleWait * 2, cap);
    continue;
}
```

- The backoff cap rises from 2 s to `MaxIdlePollIntervalWithWake` (proposed 15 s), but only when the actor confirmed
  the registration. It stays below `WakeRegistrationTtl`, so a registration never lapses while a stream is idle.
- When `WakeRegistered` is false (an older worker during a rolling upgrade, or the feature switched off), the loop
  keeps today's 2 s cap. Mixed versions are therefore safe in either order.
- `ConsumeSession` gains the same exponential backoff, which it doesn't have today.
- The existing caps (the next lock renewal, and `session_idle_timeout_seconds` for sessions) still apply.

## Failure modes

| Event | Effect | Bounded by |
| --- | --- | --- |
| Wake RPC fails or is dropped | Stream waits for its fallback poll | `MaxIdlePollIntervalWithWake` (15 s) |
| Actor deactivates or moves to another worker | In-memory waiters are lost | `OnDeactivateAsync` sends a final wake to every waiter, so streams poll and re-register on the new host. If deactivation is skipped (a crash), the fallback poll covers it |
| Worker crashes | Waiters and pending notifications are lost | Fallback poll |
| Gateway restarts at the same address | A stale waiter wakes the new process | One extra registry lookup that finds no streams. Harmless |
| Gateway is gone | Notifier sends to a dead endpoint | Send fails and is dropped. The waiter expires after `WakeRegistrationTtl` |
| Wake arrives before the stream waits | Not lost | `Subscribe` happens before the wait, and the signal latches |
| Many gateways wait on one queue | Each wakes up to `available` streams | At most gateways × `available` dequeues race for the items. The losers re-register |

None of these lose or duplicate a message. Delivery still goes only through `DequeueLocked` locks.

## Expected impact

These are targets to confirm with the perf profiles, not measurements.

| | Today | Expected |
| --- | --- | --- |
| Idle `Consume` → first message | ≈1.05 s p50, 2 s max | ≈ one gateway↔worker round trip plus a dequeue (low ms) |
| `live-publish` (sessions) delivery p50 | ≈114 ms | low ms |
| Idle actor calls per stream | 1 every 2 s | 1 every 15 s, or ≈7.5× fewer |
| Cost added to each enqueue | n/a | One dictionary check in the turn. No state I/O. Network I/O happens off the turn, and only when a waiter exists |

## Configuration

| Setting | Where | Default |
| --- | --- | --- |
| `GATEWAY_WAKE_ENDPOINT` | gateway env (Helm sets it from `POD_IP`) | unset, which turns phase 2 off for that gateway |
| `DAPRMQ_INTERNAL_TOKEN` | gateway and worker env | unset (no check) |
| `WakeRegistrationTtl` | worker, internal | 60 s |
| `MaxIdlePollIntervalWithWake` | gateway, internal | 15 s |

There are no proto changes to the public `DaprMQ` service and no SDK changes. All five SDKs benefit without a release.

## Test plan (TDD order, per CLAUDE.md)

**Integration (`./build-and-test.sh`):**
- Open a `Consume` stream on an empty queue and wait 5 s so it backs off fully. Enqueue through a *different*
  gateway, or through a topic, so the test exercises phase 2. Assert delivery within 250 ms (today ≈1 s or more).
- The same check for `ConsumeSession`.
- A deactivation check: force the queue actor to deactivate (`ACTOR_IDLE_TIMEOUT_SECONDS`), enqueue, and assert
  delivery within the fallback bound.

**Gateway (`DaprMQ.Tests`, mocked `IQueueActorInvoker`):**
- `QueueWakeRegistry`: latches a wake that arrives before the wait, wakes at most `available` streams oldest first,
  and unregisters on dispose.
- `Consume`: with `PollInterval` set high, a `Wake` causes an immediate second `DequeueLocked`. Without
  `WakeRegistered` the cap stays 2 s. `NextLockExpiresAt` shortens the wait.
- `Enqueue` handlers (REST and gRPC) call `Wake` with `itemsEnqueued` (phase 1).

**Actor (`DaprMQ.Tests`, mocked state):**
- An empty `DequeueLocked` with `WakeTarget` sets `WakeRegistered` and writes no state key.
- After that, `Enqueue` calls `Notify(target, actorId, n)` once and clears the waiter. A second `Enqueue` doesn't
  notify.
- An expired waiter isn't notified.
- With an exclusive-mode waiter, `Enqueue` while `LockCount > 0` doesn't notify, and the `Acknowledge` that brings
  `LockCount` to 0 does.
- `Nack` and `TopicActor`-driven `Enqueue` notify.
- `OnDeactivateAsync` notifies every waiter.

**Perf:** rerun the idle-streams profile (200 streams) and `live-publish` before and after. Record the results in this
document, as [consume-stream-sdks.md](../consume-stream-sdks.md) did.

## Alternatives considered

- **Actor long-poll** (`DequeueLocked{WaitSeconds}`). The wait holds the queue's turn and blocks every other caller.
  Rejected (constraint 2).
- **Dapr pub/sub broadcast.** Workers publish "queue X has items" and every gateway subscribes with its own consumer
  ID. It is simple to write, but it adds a broker dependency and fans every enqueue out to every gateway, whether or
  not that gateway has a stream waiting. Brokered at-least-once delivery is also more than a hint needs. Rejected
  (constraint 4).
- **Gateway-initiated watch streams to the hosting worker.** The actor returns its host's address, and the gateway
  opens one long-lived stream per worker to receive wakes. This avoids workers dialling gateways, but it needs
  discovery from actor to host, plus re-homing when actors move. Kept in reserve in case a deployment allows
  gateway→worker traffic but not worker→gateway.
- **Shorter polls.** Already measured: 200 idle streams at a flat 200 ms cost over a core
  ([consume-stream-sdks.md](../consume-stream-sdks.md)).

## Out of scope

- Claiming "any available" session: `AcceptSession` / `ConsumeSession` without a `session_id` waiting for *some*
  session to get work. That runs through the `SessionCoordinatorActor` ReadyQueue
  ([session-directory.md](../session-directory.md)). The same waiter pattern could be added to the coordinator later.
- Unary `Dequeue`/`DequeueLocked` long-poll for REST clients.
- A `ConsumeFlow` frame for dynamic credit, pause and drain. That is a separate item from the link-credit gap analysis.

## Open questions

1. Is 15 s the right fallback cap? It bounds the worst case after a crash. 5 s keeps more of today's safety margin for
   about half the idle savings.
2. Should phase 1 ship on its own first? It is about a day's work and fully fixes single-gateway deployments.
