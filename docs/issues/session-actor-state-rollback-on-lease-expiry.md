# Dequeuing across a session lease's expiry rolls the actor's state back and drops lease enforcement

**Status:** Confirmed, reproduced on demand. Root cause **not** established — the write-up below
documents what was measured and narrows where to look, but stops short of naming the mechanism.

**Severity:** High, with a security-adjacent edge. The lease guard is what stops a consumer that no
longer holds a session from touching it; in this state that guard disappears entirely, and a
dequeue carrying **no lease at all** succeeds. Items are also handed out repeatedly, which breaks
the at-least-once contract in the direction nobody wants.

**Blocks:** nothing outright. `S-08` passes by avoiding the trigger (see below), so this is
latent in the suite rather than failing it.

---

## Summary

While a session lease is lapsing, repeatedly dequeuing that session's queue actor puts it into a
state where:

- the **same item is handed out over and over**, each time under a fresh lock id, as though the
  preceding dequeues never happened;
- `ActiveSessionLeaseId` is **gone** from the actor's metadata, so `TryAuthorizeSessionLease`
  short-circuits to "no guard" and lets *any* caller through — wrong lease, or no lease.

The coordinator's own view stays correct throughout: `RenewSessionLease` on the old lease correctly
reports `SESSION_LEASE_EXPIRED`. It is the per-session `QueueActor` that goes wrong.

Crucially, **this does not happen if the lease is left to lapse quietly.** The same scenario with
no traffic during the expiry window behaves exactly as specified.

## Reproduction

Enqueue 3 items to session `diag`, `AcceptSession` with `leaseSeconds: 1`, then poll
`DequeueLocked` against `{queueId}-session-diag` every 250ms with the original lease id.

Busy-polling across the boundary — **broken**:

```
accepted expiresAt=…767 now=…766
poll i=0 t=…766: SUCCEEDED [seq=1/lock=aFoZBgxuRE5] locked=False
poll i=1 t=…766: SUCCEEDED []                       locked=True     <- correct: lock outstanding
poll i=2 t=…766: SUCCEEDED []                       locked=True
poll i=3 t=…766: SUCCEEDED []                       locked=True
poll i=4 t=…767: SUCCEEDED [seq=1/lock=NCZgEaz2xRv] locked=False    <- seq=1 again, LockCount reset
poll i=5 t=…767: SUCCEEDED [seq=1/lock=a4Wl3tw2FIP] locked=False
…                                                                    (repeats indefinitely)
wrong-lease dequeue: SUCCEEDED [seq=1/…]                             <- guard gone
no-lease    dequeue: SUCCEEDED [seq=1/…]                             <- guard gone
renew (old lease):   SessionLeaseExpiredException                    <- coordinator still correct
```

Note the turn at `i=4`, which is exactly when the lease expires: before it, behaviour is coherent
(item locked, subsequent dequeues correctly refused with `Locked`); after it, the actor behaves as
if rolled back to a snapshot predating every dequeue.

Quiet wait across the boundary — **correct**:

```
accepted expiresAt=…646 now=…645
(4s with no traffic)
wrong-lease dequeue: SessionLeaseExpiredException: Session lease has expired
no-lease    dequeue: SessionLeaseExpiredException: Session lease has expired
old-lease   dequeue: SessionLeaseExpiredException: Session lease has expired
```

A successful dequeue on its own does not cause it either: with a 300s lease, dequeue → then probe
with a wrong lease → still correctly `INVALID_LEASE_ID`. The trigger is specifically **traffic
concurrent with lease expiry**.

## Where to look

The guard itself is straightforward and reads correctly
([QueueActor.cs:122-149](../../server/src/DaprMQ/QueueActor.cs#L122-L149)) — it only skips
enforcement when `ActiveSessionLeaseId` is `null`. So the question is how that field became null,
given that the only code which nulls it is `ClearSessionLease`
([QueueActor.cs:334](../../server/src/DaprMQ/QueueActor.cs#L334)), and the only caller of *that* is
`ReleaseSession` — which was never invoked in the reproduction.

Ruled out by reading:

- The coordinator's session-expiry reminder
  ([SessionCoordinatorActor.cs:404-433](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L404-L433))
  removes only its own `session-lock_{id}` record. It does not call `ClearSessionLease`.
- The directory sweep does not clear leases either.
- Every metadata write on the dequeue path uses `with`, which preserves the lease fields.

The "same item, fresh lock id, `LockCount` back to zero" symptom points at the *whole* metadata
record reverting, not at the lease fields being cleared individually. That suggests a lost or
rolled-back `SaveStateAsync` — worth investigating alongside the actor's state-tracker behaviour
under a concurrent reminder turn, since the lock-expiry reminder and the lease-expiry reminder are
both plausibly firing in this window.

Given this project's history with reentrancy and reminder staleness
([DAPR_REENTRANCY_REMINDER_ISSUE.md](../DAPR_REENTRANCY_REMINDER_ISSUE.md),
[REENTRANCY_FIX_EMPIRICAL_COMPARISON.md](../REENTRANCY_FIX_EMPIRICAL_COMPARISON.md)), an
interaction between reentrancy and the state-change tracker is the first hypothesis worth testing.

## Current mitigation in the test suite

`S08_ExpiredLease_IsReclaimable_AndOldLeaseRaisesSessionLeaseExpired` waits the lease out quietly
rather than polling, with a comment pointing here. That models what a real consumer that has
stopped working does, and it exercises the documented contract — but it means the suite does **not**
currently guard against this bug. A regression test should be added as part of fixing it.

## Acceptance criteria

- Busy-polling a session queue actor across its lease expiry yields `SESSION_LEASE_EXPIRED`, the
  same as a quiet wait.
- A dequeue with no lease, or the wrong lease, is refused at every point in a lease's lifetime.
- An item already dequeued under a lock is not handed out again until that lock actually expires.
- A regression test covers the busy-poll path, and `S-08`'s quiet-wait workaround comment is removed.
