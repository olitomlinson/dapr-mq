# Dropping the per-session lease reminders

> **Status: proposed (2026-10-25).** It follows the segmented directory
> ([session-directory-segments.md](session-directory-segments.md), #109). It's independent of
> sharding the coordinator, and applies with or without it. Measurements are from kind (a laptop).

With the directory segmented, one combined release-and-claim in the r20 10,000-session run takes
21.5 ms of the coordinator's turn, mostly waiting on other services:

| Inside one combined call | Waits on | Time |
|---|---|---|
| `ClearSessionLease` on the released session's actor | that actor's turn | 3.4 ms |
| Unregistering the released session's lease reminder | the Dapr scheduler | a few ms |
| `SetSessionLease` on the claimed session's actor | that actor's turn | 3.7 ms |
| Registering the claimed session's lease reminder | the Dapr scheduler | 4.2 ms |
| The save | Postgres | 5.1 ms |

The two reminder calls are the cheapest to remove.

## Context

**What they're for.** Every claim registers a one-shot reminder, `session-{id}`, due when the lease
expires. It does useful work only when a lease lapses without a release: a consumer crashed, a
stream broke and its gateway couldn't release, a gateway died, or renewals kept failing. When it
fires, `ExpireLeaseAsync` tidies up straight away:
1. It deletes `session-lock_{id}`.
2. It reads the session actor's state.
3. It puts the session back on the ReadyQueue if it has items, or marks it Idle if not.

**Why they're mostly wasted.** Almost every lease ends with a release: the stream drains, then the
handoff or `ReleaseSession`. The release unregisters the reminder before it fires. So every session
costs two scheduler calls inside the coordinator's turn, about a third of a combined call, for a
timer that doesn't fire in normal running. A 10,000-session run creates and deletes 10,000 reminders.

**What already covers a lapsed lease without them.** The code relies on these today whenever the
scheduler is unavailable (`ClaimAsync` logs and carries on if registration fails):
- **Claims ignore a lapsed lease.** `IsLeasedAsync` compares `ExpiresAt` with the current time, so a
  targeted accept claims a lapsed session immediately.
- **The eviction pass repairs it.** Each pass puts Active, unqueued entries without a live lease back
  on the ReadyQueue, so any-available consumers get the session within one pass.
- **The session actor restores its own messages.** When its lease lapses it bulk-restores its locked
  items to the front, on its next call. Nothing is lost, and order holds.
- **A stale lease record does no harm.** It's overwritten by the next claim, or deleted by a release
  or renewal.

## Proposal

1. **Claims, renewals and releases no longer touch a lease reminder.** The `session-{id}` handler
   (`ExpireLeaseAsync`, with its re-arm) is removed.
2. **The repair reads before it requeues.** Today the pass's repair puts a lapsed session on the
   ReadyQueue without reading it ("a wrong push heals at the next lease end"). An empty session would
   then be claimed, deliver nothing, and hold a consumer slot until its idle timeout, which defaults
   to the lease length (30 s). Instead:
   - the repair reads the session's state, using the pass's existing read budget (`MaxReadsPerPass`,
     `ReadParallelism`)
   - it queues the session if it has items, and marks it Idle if it doesn't, which is what
     `ExpireLeaseAsync` did
   - a failed read errs toward availability and queues the session, as the old reminder did
3. **The repair deletes the lapsed lease record.** Both outcomes remove `session-lock_{id}`, so no
   record is left behind. Eviction also deletes any lease record of an entry it evicts.
4. **The pass runs more often** ([decision](#decision-how-soon-a-lapsed-lease-is-noticed)): `EvictionPeriod` drops from 30 s to 10 s.

## Cost and effect

- **Each combined call loses two scheduler calls,** about 7–8 ms of its 21.5 ms, so roughly 35% more
  claims per second from one coordinator.
- **The scheduler stops holding a reminder per leased session.**
- **Recovery after a crash** waits up to one pass after the lease lapses: up to 10 s at the proposed
  period, against immediately today. Normal releases are unaffected.
- **"200 means processed" is unchanged.** A lapsed session's messages are delayed, never lost.

## Decision: how soon a lapsed lease is noticed

| Option | Recovery after a crash | Cost | Discounted? |
|---|---|---|---|
| a. The pass at today's 30 s | up to 30 s after the lease lapses | none | no, but slow enough to notice |
| **b. The pass every 10 s** | up to 10 s | 3× the passes. A pass scans entries already in memory and reads only what's due (bounded by `MaxReadsPerPass`), so it's a few ms of turn every 10 s | no |
| c. One coordinator-level reminder for the earliest lease expiry | at the expiry | the reminder moves whenever the earliest expiry changes. Finding the earliest needs an index of lease expiries, like the queue actor's `locks_exp_{bucket}` | **yes, for now**: an index to keep, for seconds of recovery |
| d. Keep per-session reminders | at the expiry | two scheduler calls per claim, about a third of it | **yes**: the cost this proposal removes |

**Recommend b.** If 10 s proves too slow for some workload, c is the way to get prompt recovery
without the per-claim cost.

## Tests (TDD order, per CLAUDE.md)

1. **Integration:** a session whose consumer stops without releasing is claimed again by an
   any-available accept within one pass after its lease lapses, with its messages in order.
2. **Coordinator:**
   - a claim, renewal or release registers and unregisters no lease reminder
   - the pass's repair of a lapsed session:
     - reads it, then queues it if it has items, or marks it Idle if it doesn't
     - deletes its lease record in both cases
     - queues it if the read fails
     - stays within the read budget, finishing on the catch-up pass
   - eviction deletes an evicted entry's lease record

## Measuring it

- **The workload it targets.** r20, 10,000 sessions of 10, before and after, with the timing build.
  Compare the combined call's time, coordinator busy time and throughput.
- **No regression.** r20, 1,000 sessions of 100.
- **Recovery.** Delete a consumer pod mid-drain, so its sessions' leases lapse unreleased. Time from
  each lease's expiry to its session's next claim (expected ≤ 10 s). Check that everything drains.

## Related work

- [session-directory-segments.md](session-directory-segments.md): the change that made these calls
  the larger part of a claim.
- [session-coordinator-sharding.md](session-coordinator-sharding.md): the other way to raise the
  coordinator's claim rate, by running more coordinators.
- [async-session-registration.md](async-session-registration.md): the session actor's own
  registration backstop reminder, which this doesn't touch.
