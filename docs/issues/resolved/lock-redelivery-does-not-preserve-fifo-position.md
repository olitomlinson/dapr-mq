# Redelivery after lock expiry appends to the back of the queue on plain queues

**Status:** Resolved 2026-09-30. Both tiers now restore an expired lock's item to the position it was
taken from. The product decision this was blocked on was taken: **option A, sequence numbers.**

**Severity:** was Medium.

Everything from here to the Resolution describes the state *before* the fix and is kept as the record
of what was wrong and why the obvious fix wasn't one. `ExpireLockAsync` and `RequeueFrontInternal`
no longer exist; see the Resolution for what replaced them.

---

## Summary

On a **plain queue**, a locked item whose TTL lapses without an acknowledgement is put back at the
**tail** of its priority. An item that expires at the head of `[1, 2, 3]` comes back as `[2, 3, 1]`.

On a **session queue** the same situation restores every outstanding lock to the **front**, in the
original order, when the session lease lapses.

## Behaviour before the fix, as verified on the merged tree

| | plain queue | session queue |
|---|---|---|
| trigger | per-lock `ExpiresAt`, swept lazily | session **lease** lapse (locks have no per-item expiry) |
| path | [`ExpireLockAsync`](../../../server/src/DaprMQ/QueueActor.cs#L776) → `EnqueueInternal` | [`ReclaimLapsedSessionLocksAsync`](../../../server/src/DaprMQ/QueueActor.cs#L624) → [`RequeueFrontInternal`](../../../server/src/DaprMQ/QueueActor.cs#L713) |
| position | tail | front, relative order preserved |
| unit test | [`Sweep_RequeuesExpiredLockToTail_AndClearsItsIndexEntry`](../../../server/tests/DaprMQ.Tests/QueueActorLockExpiryTests.cs#L236) | [`LapsedSessionLease_BulkRequeuesLocksToTheFrontInOrder`](../../../server/tests/DaprMQ.Tests/QueueActorLockExpiryTests.cs#L600) |

Also landed with the sweep, and worth recording because it closes two things this issue previously
listed as missing:

- `DeliveryCount` on both `LockState` and `QueueSegmentItem`, with `LockConfig.MaxDeliveryCount`
  (default 10) routing a poison item to the DLQ instead of requeueing it — Service Bus parity, and
  the thing that stops a position-preserving redelivery pinning a bad item at the head forever.
- Locks are enumerable via the `locks_exp_{bucket}` / `locks_session` index, so expiry is reachable
  without a per-lock reminder. **There are no lock reminders any more**; `QueueActor` is no longer
  `IRemindable`. Earlier revisions of this issue described a `ReceiveReminderAsync` path — that is
  gone.

## Evidence

`L08_RedeliveryAfterExpiry_ReturnsTheItemToTheBackOfItsPriority`
([LockTests.cs:203](../../../sdks/dotnet/tests/DaprMQ.Client.IntegrationTests/LockTests.cs#L203))
enqueues `1, 2, 3`, locks item 1 with a 1s TTL, never acks, then dequeues 3 — and gets `[2, 3, 1]`.
The test asserts the observed order and passes. The unit-level equivalent at
[QueueActorLockExpiryTests.cs:236](../../../server/tests/DaprMQ.Tests/QueueActorLockExpiryTests.cs#L236)
pins the same thing directly.

## Cause

One call, [QueueActor.cs:786](../../../server/src/DaprMQ/QueueActor.cs#L786):

```csharp
bool requeued = await EnqueueInternal(lockData.ItemJson, lockData.Priority, deliveryCount);
```

The comment above it is explicit that this is a carry-over: *"Plain queues requeue to the tail,
matching the behaviour the per-lock reminder had"*
([QueueActor.cs:773](../../../server/src/DaprMQ/QueueActor.cs#L773)).

## Why pointing it at `RequeueFrontInternal` is not sufficient

`RequeueFrontInternal` allocates **fresh segments strictly below the current head** —
`int firstSegment = headSegment - chunks;`
([QueueActor.cs:738](../../../server/src/DaprMQ/QueueActor.cs#L738)) — and never merges into the
existing head segment. So each invocation lands below whatever the previous invocation restored.

The session path gets away with this because a lapsed lease drains the entire `locks_session` index
in **one uncapped batch** ([QueueActor.cs:642](../../../server/src/DaprMQ/QueueActor.cs#L642)) — there
is exactly one invocation, so there is nothing to order against.

Plain-queue expiry is neither single-shot nor uncapped. The sweep expires locks one at a time under a
`SweepBatchSize` budget (default 200,
[QueueActor.cs:534](../../../server/src/DaprMQ/QueueActor.cs#L534)), and any remainder is left for the
next operation. Two consequences:

1. **Budget split inverts.** 300 locks expire together; sweep 1 restores items 1–200, sweep 2
   restores 201–300 *below* them → `[201…300, 1…200]`.
2. **Sequential expiry inverts completely.** Locks with staggered TTLs expire across separate
   sweeps, so each restored item lands in front of the one that expired before it — exact reversal.

The correct insertion point is therefore not "the front" but **the boundary between already-restored
items and never-locked items**, which nothing currently tracks.

To be clear about the standing of this: it is **not an observable defect today**. It is a property
of the proposed fix, established by reading `firstSegment = headSegment - chunks` rather than by a
failing test, and it cannot be provoked against the current code because the only caller of
`RequeueFrontInternal` is single-shot. The session path is additionally safe by accident: successive
lease lapses restore progressively shorter *prefixes* of the queue, so each restored batch really is
earlier than everything already queued. A budget-split plain-queue restore is the opposite — the
second batch is later than the first — which is why the same primitive breaks there.

## The ways out

**A. Sequence numbers.** Give `QueueSegmentItem` a monotonic per-priority `Seq`, carry it on
`LockState`, and insert by `Seq`. Correct under any expiry order, any batching, any mode — this is
how Service Bus gets it for free. Costs a new counter in `QueueMetadata`, an ordered-insert path, and
a back-compat story for items written without a `Seq`.

**A′. Restore cursor.** Track, per priority, the segment/offset boundary of the restored region and
append into it. Cheaper than A but it is a second ordering invariant to keep correct across offload,
drain and head advancement.

**A″. Never vacate the slot.** Stop removing the item at lock time; mark it locked in place and have
`Acknowledge`/`DeadLetter` do the removal. The reinsertion problem cannot arise, because no position
is ever given up. Biggest diff (dequeue must skip locked items, mid-segment removal, head
advancement, an offload guard for segments holding live locks, migration for locks written under the
current model), but it removes an invariant rather than adding one.

**B. Document the two-tier contract.** Keep the tail append for plain queues and state it: strict
FIFO on redelivery is a **session** guarantee. Qualify `CLAUDE.md:56` (*"FIFO preservation"* under
**Locks**) and the README's ordering-versus-Kafka framing accordingly. Cheap, defensible, and it
describes what the code now actually does on both paths.

Note that B is a materially stronger position than it was before the sweep landed: the strict
guarantee now genuinely exists, it is just scoped to sessions.

## Acceptance criteria

- **If A/A′/A″:** `[1, 2, 3]` with an expired head redelivers as `[1, 2, 3]` on a plain queue. Flip
  and rename `L08_…` and `Sweep_RequeuesExpiredLockToTail_…`, update the `L-08` matrix row, and
  **cover the two inversion cases above explicitly** — a backlog larger than `SweepBatchSize`, and
  locks expiring in separate sweeps.
- **If B:** qualify the claim in `CLAUDE.md` and re-check the README. Both existing tests and the
  `L-08` row already describe the behaviour correctly and need no change.

---

## Resolution

**Fixed** (2026-09-30). Option A, as laid out above, with one correction to its framing discovered
during implementation — see below.

- Every item carries a `required long Sequence`, stamped in `EnqueueInternal` from an actor-wide
  `ActorMetadata.NextSequence` and never reassigned. `LockState` carries it across the
  dequeue → lock → restore round trip, exactly as `DeliveryCount` already did.
- `RequeueFrontInternal` became `RestoreInOrderInternal`: it merges reclaimed items into the **head
  segment** by `Sequence` rather than prepending fresh segments below it, spilling into
  `HeadSegment - 1, -2, …` only on overflow. Both the plain-queue sweep and the session lease-lapse
  path use it, so the two-tier contract is gone rather than documented.
- `SweepExpiredLocksAsync` collects across every due bucket and restores once, instead of calling a
  per-lock `ExpireLockAsync` (now deleted) that rewrote the queue for each item.
- Dead-lettering on `MaxDeliveryCount` moved after the sweep's local commit, matching what the
  session path already did — it reaches another actor and commits per item.

### The correction

The plan justified merging into the head segment with "restored items always belong ahead of
everything still queued". That is **false** once a restore has already happened: locks 1–2 lapsing
and being restored puts items 1–2 back *in* the queue while 3–5 are still locked, so the next
restore's items do not all precede what is queued. A concat would reverse them; the merge is what
actually earns the correctness, and `Sweep_LocksExpiringInSeparateSweeps_RestoreInOrder` pins it.

The *bound* the plan claimed still holds, for a different reason: an item in a segment beyond the
head has never been dequeued, because restores only ever write at or below the head. So anything
reaching the restore carries a lower `Sequence` than anything past the head segment, and merging
into the head segment alone is sufficient.

### Tests

New in [QueueActorLockExpiryTests.cs](../../../server/tests/DaprMQ.Tests/QueueActorLockExpiryTests.cs):
single expired head restores to position; backlog larger than `SweepBatchSize`; locks expiring in
separate sweeps; restore stays within its own priority; restore into a full head segment spills
below it.

Flipped, having pinned the tail append under names that described the opposite:
`L08_RedeliveryAfterExpiry_ReturnsTheItemToTheBackOfItsPriority` →
`…_PreservesTheItemsFifoPosition`, plus `QueueActorTests.ExpiredLock_PreservesQueuePosition` and
`QueueActorTests.LockExpiry_DoesNotReorderQueue`.

`LapsedSessionLease_DrainedPriority_RestoresBelowSegmentZero` →
`…_RestoresIntoTheLiveRange`: a drained priority now reuses the vacated head slot instead of
allocating below it.

### Not fixed by this change

Competing consumers can still overtake a live lock — position is restored, causal order is not,
matching Service Bus's non-session queues. Callers who need strict order either leave
`AllowCompetingConsumers` off (the default path blocks on any outstanding lock) or pass
`MaxConcurrency: 1`. That is a per-request parameter rather than a queue-level setting, so it holds
only while every caller passes it; making it an invariant is a separate change.

### Breaking

Pre-1.0, deliberately unmigrated: `Sequence` is `required`, so a queue holding items written before
this change fails to deserialise rather than misordering them. Drain or recreate existing queues.
