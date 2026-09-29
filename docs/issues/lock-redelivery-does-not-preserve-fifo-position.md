# Redelivery after lock expiry appends to the back of the queue instead of preserving position

**Status:** Confirmed. Behaviour is unambiguous and the cause is a single line. What is *not*
settled is which side is wrong — the code or the contract. **This one needs a product decision
before any code changes.**

**Severity:** Medium, but it depends entirely on that decision. If FIFO-on-redelivery is meant to
hold, this silently violates the project's central ordering promise. If it is not, three documents
are making a claim the system does not deliver.

---

## Summary

When a locked item's TTL expires without an acknowledgement, the item is put **back at the tail of
its priority queue**, not back where it was. An item that expires at the head of `[1, 2, 3]` comes
back as `[2, 3, 1]`.

## Evidence

`L08_RedeliveryAfterExpiry_ReturnsTheItemToTheBackOfItsPriority` in
[LockTests.cs](../../sdks/dotnet/tests/DaprMQ.Client.IntegrationTests/LockTests.cs):

1. Enqueue items `1, 2, 3` at the same priority.
2. `DequeueLocked(count: 1, ttlSeconds: 1)` — takes item 1, leaves a lock outstanding.
3. Never acknowledge. While that lock is held the queue withholds everything else, so the next
   non-empty dequeue is necessarily the whole post-expiry queue.
4. `DequeueLocked(count: 3)` returns **`[2, 3, 1]`**.

The test asserts the observed order and passes.

## Cause

[QueueActor.cs:922](../../server/src/DaprMQ/QueueActor.cs#L922), in the lock-expiry reminder:

```csharp
bool success = await EnqueueInternal(lockState.Value.ItemJson, lockState.Value.Priority);
```

`EnqueueInternal` is a plain append. The original position is not recorded anywhere on the lock —
`LockState` carries `Priority` but nothing about where in the segment the item came from — and
`DequeueLocked` genuinely removes the item from the queue when it locks it
([QueueActor.cs:1045](../../server/src/DaprMQ/QueueActor.cs#L1045): *"Dequeue item (removes from
queue) and store in lock"*), so there is no reserved slot to put it back into.

## Why this is a contract question, not just a bug

Three places state or imply the opposite:

- [sdks/testing/INTEGRATION_TESTS.md](../../sdks/testing/INTEGRATION_TESTS.md) — row `L-08` was
  originally *"Redelivery preserves original FIFO position"*. Now reworded, with a note.
- [CLAUDE.md](../../CLAUDE.md) — *"Locks: In-place with `LockId`. Enables DLQ routing, lock
  extension, FIFO preservation."*
- [README.md](../../README.md) — positions per-queue ordering as the core differentiator against
  Kafka's per-partition ordering.

Against that, note that in the default (non-competing-consumer) mode a single outstanding lock
blocks all other dequeues, so nothing can overtake the locked item *while the lock is live*.
Ordering is only broken at the moment of expiry. That may well be an acceptable trade — a consumer
that failed to ack has arguably forfeited its place — but it should be a stated one.

## The two ways out

**A. Make the code match the contract.** Preserve position on redelivery. This is the more
expensive option: it needs either a reserved slot left in the segment at lock time, or a
front-insert path on the priority queue, plus a decision about what happens when several locks from
the same batch expire together (their relative order has to be preserved too). Touches the
segmented-storage invariants, so it wants care.

**B. Make the contract match the code.** Keep the append and document it — "an expired lock's item
rejoins at the tail of its priority" — in `CLAUDE.md`, the matrix row, and anywhere the README
implies otherwise. Cheap, and defensible.

## Acceptance criteria

Depends on the decision:

- **If A:** `[1, 2, 3]` with an expired head redelivers as `[1, 2, 3]`. Flip the assertion in
  `L08_…` (and rename it), update the matrix row back, and cover the multi-lock case.
- **If B:** update `CLAUDE.md` and re-check the README's ordering claims. The existing test and
  matrix row already describe the behaviour correctly and need no change.
