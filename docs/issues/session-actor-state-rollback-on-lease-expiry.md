# Reclaiming a lapsed session's locks clears the lease, dropping lease enforcement and redelivering items

**Status:** Confirmed, reproduced on demand. Root cause identified (below). Not fixed.

**Severity:** High, with a security-adjacent edge. The lease guard is what stops a consumer that no
longer holds a session from touching it; once this triggers, the guard is gone and a dequeue carrying
**no lease at all** succeeds. Every such dequeue also burns a delivery attempt, so a healthy item is
dead-lettered after `MaxDeliveryCount` (default 10) polls.

**Blocks:** nothing outright. `S-08` passes by avoiding the trigger (see below), so this is latent in
the suite rather than failing it.

---

## Summary

When a session lease lapses while the session actor still holds at least one lock, the next operation
that sweeps (Dequeue, DequeueLocked, SetSessionLease, or activation) reclaims the locked items, which
is correct, and also **nulls `ActiveSessionLeaseId`**, which is not. From then on:

- `TryAuthorizeSessionLease` sees no lease and lets **any** caller through, whether it presents the
  old lease, the wrong lease, or none at all.
- The reclaim runs again on every following dequeue, because a null lease never counts as live. The
  item just handed out is pulled back to the front and handed out again under a new lock id, with
  `DeliveryCount` going up by one each time until the item is dead-lettered.

The coordinator is unaffected: `RenewSessionLease` on the old lease still reports
`SESSION_LEASE_EXPIRED`.

This is not a state rollback. The original write-up assumed one because the actor looked as though it
had reverted to a snapshot from before the first dequeue. The behaviour is fully deterministic and
follows from the code path below.

## Root cause

`ReclaimLapsedSessionLocksAsync`
([QueueActor.cs:686-760](../../server/src/DaprMQ/QueueActor.cs#L686-L760)) clears the lease fields as
part of its commit
([QueueActor.cs:738-745](../../server/src/DaprMQ/QueueActor.cs#L738-L745)):

```csharp
await SetMetadataAsync(metadata with
{
    LockCount = Math.Max(0, metadata.LockCount - (restored.Count + deadLettered.Count)),
    ActiveSessionLeaseId = null,
    ActiveSessionLeaseExpiresAt = null
});
```

`DequeueLocked` sweeps first and only then authorizes
([QueueActor.cs:1472](../../server/src/DaprMQ/QueueActor.cs#L1472), then
[QueueActor.cs:1482](../../server/src/DaprMQ/QueueActor.cs#L1482)). `Dequeue` does the same. The guard
treats a null lease as "not a leased session" and returns `true`
([QueueActor.cs:283-288](../../server/src/DaprMQ/QueueActor.cs#L283-L288)), so the sweep strips the
lease and the guard then lets the same call through.

The reclaim exits early only when the lease is live or when `LockCount == 0`
([QueueActor.cs:689-702](../../server/src/DaprMQ/QueueActor.cs#L689-L702)). Once the lease is null it
is never live again, so every dequeue that leaves a lock behind sets up another reclaim on the next
call.

This code has been present since the initial commit (`bf0fc4c`). `ClearSessionLease` is **not** the
only code that nulls the lease, as the original write-up claimed.

## Reproduction

Enqueue 3 items to session `diag`, `AcceptSession` with `leaseSeconds: 1`, then poll `DequeueLocked`
against `{queueId}-session-diag` every 250ms with the original lease id.

```
accepted expiresAt=…767 now=…766
poll i=0 t=…766: SUCCEEDED [seq=1/lock=aFoZBgxuRE5] locked=False   <- lock taken, LockCount=1
poll i=1 t=…766: SUCCEEDED []                       locked=True    <- lease live, reclaim no-ops
poll i=2 t=…766: SUCCEEDED []                       locked=True
poll i=3 t=…766: SUCCEEDED []                       locked=True
poll i=4 t=…767: SUCCEEDED [seq=1/lock=NCZgEaz2xRv] locked=False   <- lease lapsed: reclaim restores
                                                                      seq=1 and nulls the lease, so
                                                                      the guard passes
poll i=5 t=…767: SUCCEEDED [seq=1/lock=a4Wl3tw2FIP] locked=False   <- null lease isn't live: reclaim
…                                                                     again, DeliveryCount+1 each time
wrong-lease dequeue: SUCCEEDED [seq=1/…]                            <- no lease to enforce
no-lease    dequeue: SUCCEEDED [seq=1/…]
renew (old lease):   SessionLeaseExpiredException                   <- coordinator state is correct
```

The same scenario with **no dequeue before the lapse** behaves correctly:

```
accepted expiresAt=…646 now=…645
(4s with no traffic)
wrong-lease dequeue: SessionLeaseExpiredException: Session lease has expired
no-lease    dequeue: SessionLeaseExpiredException: Session lease has expired
old-lease   dequeue: SessionLeaseExpiredException: Session lease has expired
```

Here `LockCount` is 0, so the reclaim returns early, the stale lease stays in metadata, and the guard
correctly reports it expired. The quiet wait avoids the bug because nothing was locked, not because
there was no traffic. **The trigger is an outstanding lock at the moment the lease lapses, not traffic
concurrent with expiry.** A consumer that dequeues once and then dies hits this on the next caller's
first dequeue, however long that is after the lapse.

## Fix

In `ReclaimLapsedSessionLocksAsync`, keep `ActiveSessionLeaseId` and `ActiveSessionLeaseExpiresAt` as
they are, and touch only `LockCount`. Leaving them in place is safe:

- The lapsed expiry already makes the guard return `SESSION_LEASE_EXPIRED` to every caller until a new
  holder arrives.
- `SetSessionLease` overwrites both fields for the next holder, after running its own sweep
  ([QueueActor.cs:487-502](../../server/src/DaprMQ/QueueActor.cs#L487-L502)).
- The reclaim deletes the session lock index
  ([QueueActor.cs:736](../../server/src/DaprMQ/QueueActor.cs#L736)), and the guard stops any new lock
  being taken under the lapsed lease. A second pass therefore finds `LockCount == 0` and does nothing,
  so the reclaim runs once per lapse.

`ClearSessionLease` stays the only code that nulls the lease, which is its documented role (explicit
release).

## Current mitigation in the test suite

`S08_ExpiredLease_IsReclaimable_AndOldLeaseRaisesSessionLeaseExpired`
([SessionTests.cs:154](../../sdks/dotnet/tests/DaprMQ.Client.IntegrationTests/SessionTests.cs#L154))
waits the lease out quietly. The test never dequeues before the wait, so it would avoid the bug even
with polling. Its comment blames a state rollback and is wrong.

## Acceptance criteria

- **Actor test:** dequeue under a lease, let the lease lapse, then dequeue twice. Both dequeues return
  `SESSION_LEASE_EXPIRED`, the item's `DeliveryCount` goes up by exactly 1, and a no-lease dequeue is
  refused.
- **Guard:** a dequeue with no lease, or with the wrong lease, is refused at every point in a lease's
  lifetime until `ReleaseSession` is called.
- **Redelivery:** an item is redelivered once per lapse, not once per poll.
- **Integration test:** S-08, or a new test beside it, dequeues under the first lease *before* the
  lapse and busy-polls across it. The rollback comment in S-08 is removed.
