# Reentrancy fix: defaultTracker staleness round-trip impact

**Status:** Informational — documents a known, accepted tradeoff of the reentrancy fix in
`docs/DAPR_REENTRANCY_REMINDER_ISSUE.md`. Not a bug. For triaging future performance questions
("why did Postgres load go up", "why is this actor slower than expected") against a known cause.

**Context:** After enabling `options.ReentrancyConfig` (see `server/src/DaprMQ.ApiServer/Program.cs`)
with the patched `Dapr.Actors` SDK (`1.18.4-reentrancyfix.1`, `server/nuget-local/`), actor state
reads that used to be served from an in-memory cache can now, in specific circumstances, force a
real read against the state store instead. This document is the concrete list of where that
happens in DaprMQ, how often, and what it actually costs.

---

## The mechanism

Every actor instance's `ActorStateManager` keeps a `defaultTracker` dictionary — a client-side
cache of state key values. Normally, once a key is read once during an activation, later reads of
that same key (across many separate method calls, for the life of the activation) are served from
this dictionary with zero I/O.

The original bug (`docs/DAPR_REENTRANCY_REMINDER_ISSUE.md`) was that `defaultTracker` was never
invalidated when a *different* call path wrote the same key — specifically, reminder/timer/
activation callbacks (which never carry a `Dapr-Reentrancy-Id` header) share the actor instance
with ordinary method calls (which, once reentrancy is enabled for the actor type, always do carry
that header and so always get their own fresh, isolated, throwaway tracker — see
`GetContextualStateTracker()` in the patched SDK). A write via the throwaway tracker was invisible
to `defaultTracker`, so a reminder could read stale data from `defaultTracker` and silently no-op.

The fix adds eviction: when a contextual (reentrant) tracker's `SaveStateAsync` commits, it drops
any *clean* copy of the same key(s) from `defaultTracker`:

```csharp
// src/Dapr.Actors/Runtime/ActorStateManager.cs (patched, actors-fix-state branch)
if (!ReferenceEquals(stateChangeTracker, this.defaultTracker))
{
    this.InvalidateDefaultTracker(stateChangeList);
}
...
private void InvalidateDefaultTracker(IEnumerable<ActorStateChange> stateChanges)
{
    foreach (var stateChange in stateChanges)
    {
        if (this.defaultTracker.TryGetValue(stateChange.StateName, out var stateMetadata) &&
            stateMetadata.ChangeKind == StateChangeKind.None)
        {
            this.defaultTracker.Remove(stateChange.StateName);
        }
    }
}
```

The next time something reads that key via `defaultTracker` (i.e. the next activation, reminder,
or timer invocation that touches it), it's a cache miss — a real round trip — and the fresh value
is re-cached as clean immediately afterward:

```csharp
var conditionalResult = await this.TryGetStateFromStateProviderAsync<T>(stateName, cancellationToken);
if (conditionalResult.HasValue)
{
    stateChangeTracker.Add(stateName, StateMetadata.Create(conditionalResult.Value.Value, StateChangeKind.None, ...));
    ...
}
```

**Scope — this is not limited to true reentrant (A → B → A) call chains.** Once
`options.ReentrancyConfig.Enabled = true` for an actor type, `daprd` assigns a fresh
`Dapr-Reentrancy-Id` to *every* ordinary method invocation on that type, not only ones that happen
to nest into another call. A single, standalone, never-nested `Publish()` call gets a reentrancy ID
exactly like a call that's genuinely part of an A → B → A chain — and it's the presence of that ID
(routing the call through a throwaway contextual tracker instead of `defaultTracker`), not whether
the call is actually "reentrant" in the nested sense, that triggers eviction on save. Reminders and
timers never carry the header regardless of how they were scheduled. So the real trigger condition
is simply: **does any ordinary call and any reminder/timer/activation callback write the same key
on the same actor instance?** — A → B → A (`SessionCoordinatorActor`'s deadlock) is the scenario
that motivated turning reentrancy on in the first place, but it plays no role in *this* mechanism.
That's why the impact list below includes plain, never-nested calls like `Publish`, `Enqueue`, and
`InitializeHttpSink` — none of those are reentrant chains themselves.

**Important properties of this cost:**

- It is **self-healing**, not cumulative: one round trip pays it off, then the entry is clean again
  until the next cross-tracker write. It does not cascade into further reloads on its own.
- It **only affects `defaultTracker` consumers** — activation, reminder callbacks, timer
  callbacks. Ordinary method calls are unaffected in either direction: under reentrancy they
  already get a brand-new, empty, throwaway tracker per call (`SetStateContext` creates
  `new Dictionary<string, StateMetadata>()` every time), so they never benefited from any
  cross-call caching to begin with, before or after this fix.
- It only ever evicts the **specific key(s)** just written elsewhere — not the whole tracker.

## Where the round trip actually goes

The call chain on a forced reload is: app process → `daprd` (same host/pod, genuinely local,
cheap) → `daprd` → the actual state store. **`daprd` does not cache actor state reads itself** —
every cache miss at the app SDK layer is a live query against the backing store.

In DaprMQ's integration tests this second hop is also local (Postgres runs as a sibling container
on the same Docker network), which makes it easy to mistake the whole thing for "cheap". In
production it is not: `server/dapr/helm-components/statestore-k8s.yaml` points `daprd` at
`my-release-postgresql.default.svc.cluster.local`, an in-cluster Kubernetes Postgres service —
Postgres runs in a different pod, reached over the cluster's pod network via a Service. That's a
genuine network + database round trip, not a same-host call. Latency depends on that specific
cluster's conditions (Postgres load, connection pooling, network conditions) — not something this
document can quantify, but it should never be assumed negligible.

At scale, each forced reload is one extra query against the production Postgres instance, not just
added latency on the read path — it's added steady-state load on the database, proportional to how
often the affected paths below actually fire.

---

## Concrete impact list, by actor

Every registered actor type mixes reminder/timer-driven state writes with ordinary-call state
writes on the same key(s) somewhere — that's a common actor pattern, and it means every one of
them has *some* exposure. Verified by tracing every `ReceiveReminderAsync` dispatch target's state
key usage against every ordinary method's, for all 5 actor types registered in
`Program.cs`.

### QueueActor — highest-traffic actor

| Key | Reminder-path writer | Ordinary-path writer(s) |
|---|---|---|
| `metadata` (`ActorMetadata`) | Lock-expiry reminder (`QueueActor.cs:954`) | Enqueue, Dequeue, DequeueLocked, Acknowledge, ExtendLock, DeadLetter, SetSessionLease, ClearSessionLease |
| `queue_{priority}_seg_{tailSegment}` | Lock-expiry reminder's re-enqueue, via `EnqueueInternal` (`QueueActor.cs:417`) | Enqueue (when it lands on the same tail segment) |

Lock-expiry reminders are **one-shot**, firing only when a `DequeueLocked` lock isn't acknowledged
before its TTL — a backstop/failure path, not proportional to raw throughput. Cost scales with how
many locks go unacknowledged (e.g. a slow or failing consumer), not with message volume.

### TopicActor — the standout (see below)

| Key | Reminder-path writer | Ordinary-path writer(s) |
|---|---|---|
| `MetadataKey` | `RelayTickAsync`, `ReapItemsTickAsync` | Subscribe, Unsubscribe, Publish |
| `ItemKey(sequence)` | `ReapItemsTickAsync` (removes) | Publish (writes) |
| `PublishKey(publishId)` | `ReapItemsTickAsync` (removes) | Publish (writes) |
| `PublishSeqKey(sequence)` | `ReapItemsTickAsync` (removes) | Publish (writes) |
| `GenerationKey(generationId)` | `ReapGenerationTickAsync` (removes) | Subscribe, Unsubscribe (write) |
| `CircuitKey(subscriberId)` | `RelayTickAsync`, via `RecordFailureAsync`/`ClearCircuitBreakerAsync` | ResetCircuitBreaker (removes) |

### SessionCoordinatorActor

| Key | Reminder-path writer | Ordinary-path writer(s) |
|---|---|---|
| `metadata` (`SessionCoordinatorMetadata`) | TTL sweep (`SweepDirectoryAsync`) | RegisterSession, AcceptSession |
| `session-lock_{sessionId}` | Per-session lease-expiry reminder (`SessionCoordinatorActor.cs:415-418`) | RenewSessionLease, ReleaseSession, AcceptSession |

Reminder paths here are TTL/lease-driven, comparatively low-frequency.

### HttpSinkActor

| Key | Reminder-path writer | Ordinary-path writer(s) |
|---|---|---|
| `StateKey` (single state blob) | `PollAndDeliver`, via `UpdatePollingInterval` | InitializeHttpSink, UninitializeHttpSink |

Poll-interval driven, low-frequency relative to TopicActor's tick.

### BlobReaperActor

| Key | Reminder-path writer | Ordinary-path writer(s) |
|---|---|---|
| `StateKey` (`BlobReaperActorState`) | `ReceiveReminderAsync` / `StopReapingAsync` | ScheduleDeletion, PostponeDeletion |

`AttemptCountKey` is **not** affected — only ever touched from inside the reminder callback,
never from an ordinary call, so it never crosses trackers.

---

## Standout: TopicActor's `RelayTickAsync` ↔ `Publish()` on `MetadataKey`

Every other reminder path on this list is **one-shot**, triggered by a discrete failure/expiry
event (lock TTL, blob-delete retry, session-lease expiry, TTL sweep) — bounded, occasional cost.

TopicActor's relay reminder is different. Per `TopicActor.cs:658-667`:

```csharp
int nextInterval = enqueueTasks.Count > 0
    ? 1
    : Math.Min(metadata.CurrentRelayIntervalSeconds * 2, _config.RelayIntervalCeilingSeconds);
...
await TryRegisterReminderAsync(RelayReminderName, nextIntervalSpan, nextIntervalSpan);
```

As long as there's pending relay work (`enqueueTasks.Count > 0`), the reminder re-registers itself
at a **1-second interval, indefinitely**. And the condition that keeps it at 1 second — active
relay work — is exactly correlated with the condition that makes `Publish()` frequently write the
same `MetadataKey`: a topic being actively published to.

Net effect: on any topic under steady publish traffic, expect roughly **one extra Postgres query
per second, for as long as that topic stays busy** — not a bounded, one-off cost like everything
else on this list, but a standing per-second tax that scales with however many topics are hot
concurrently (10 busy topics ≈ 10 extra queries/sec against production Postgres, continuously).

It backs off correctly when idle (`SubscriberIds.Count == 0` skips re-registration entirely;
otherwise the interval doubles up to `RelayIntervalCeilingSeconds` once there's no pending work),
so this is specifically an *active-traffic* cost, not a permanent one per topic ever created.

---

## What the fix does *not* address

Not evaluated end-to-end in DaprMQ, documented here for awareness:

- **A narrower last-writer-wins race.** Eviction only fires for `ChangeKind.None` (clean) entries
  in `defaultTracker` — it never touches an actor's own pending, uncommitted write in
  `defaultTracker`. If `defaultTracker` already has an uncommitted write to a key at the exact
  moment a reentrant call's save lands, that pending write is left alone and will overwrite the
  reentrant write when `defaultTracker` eventually saves. Per Dapr's per-instance call
  serialization (documented in `docs/DAPR_REENTRANCY_REMINDER_ISSUE.md` Phase 3 — one call chain
  occupies an actor instance's lock at a time, reentrancy only lets a chain re-enter *itself*),
  this shouldn't be reachable across genuinely unrelated call chains in DaprMQ's actors — but this
  has not been proven with a dedicated test the way `SessionReentrancyTests` proved the A→B→A
  deadlock fix.
- **Reentrant-vs-reentrant staleness.** Two concurrent reentrant call chains each get their own
  isolated tracker; the invalidation logic only reaches into `defaultTracker`. Out of scope for
  this patch by design (it targets default-tracker-vs-reentrant staleness specifically).

---

## Triage checklist for future investigations

If Postgres load or actor state-read latency looks higher than expected after this change:

1. Check whether the affected actor type is one of the five above, and whether the specific key
   involved is one of the shared ones listed.
2. For TopicActor specifically: correlate with publish volume / number of concurrently "hot"
   topics (topics with pending relay work) — this is the one load-scaling case, not just
   frequency-of-event scaling.
3. For QueueActor: correlate with lock-expiry rate (unacknowledged `DequeueLocked` locks) — a
   spike usually means a consumer is slow or failing, not a QueueActor problem per se.
4. This is a known, accepted tradeoff (silently losing writes vs. an occasional extra local/DB
   round trip) — the fix should not be reverted or reentrancy disabled to "solve" this without
   reintroducing the original deadlock (`docs/DAPR_REENTRANCY_REMINDER_ISSUE.md`) and reminder
   staleness bug it fixes.
