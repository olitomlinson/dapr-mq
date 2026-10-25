# Registering sessions off the enqueue path

> **Status: proposed (2026-10-25).** This comes first among the session-throughput work. It is phase
> 1b, item 2, of [session-coordinator-sharding.md](session-coordinator-sharding.md), brought forward
> ahead of the segmented directory ([session-directory-segments.md](session-directory-segments.md)).
> Measurements are from kind (a laptop).

## Context

When a session actor makes its first call with work in an activation, it registers itself with
the queue's `SessionCoordinatorActor` from inside its own turn ([QueueActor.cs](../server/src/DaprMQ/QueueActor.cs),
`AfterActorMethodAsync`). The enqueue that caused the call doesn't return until the registration
does. That makes a new session claimable the moment its first enqueue returns. It also makes every
publisher wait on the coordinator, which runs one turn at a time for the whole queue.

The worker's `SessionActivityReporter` already does the same job in batches. It sends every
session's has-work changes for a queue as one `ReportSessionChanges` call: one turn and one save
for the whole batch. But it only sends on a 10 s tick, so registration today is "sync, with the
reporter as fallback".

### What it costs

r20, 100k messages over 10,000 sessions of 10 each, with each coordinator call timed:

| | Before one save per turn | After one save per turn |
|---|---|---|
| Throughput | ~131 msg/s (timed out at 600s with 80,199 handled) | 141 msg/s |
| Publishing | 8,040 of 10,000 sessions in 600s | 10,000 in 706s |
| `RegisterSession`: the gateway's wait / time inside the turn | 1,056 ms / 19 ms | 833 ms / 21 ms |
| `RegisterSession`'s share of coordinator time | 23% | 33% |
| Coordinator busy | 602 of 614 s | 692 of 708 s |

- **Throughput is the publish rate.** The drain finished 2s after the last publish.
- **Each enqueue that starts a session waits about 1s** on the coordinator. Ten publishers can then
  start only about 13 sessions a second.
- **Registrations are one turn per session.** That's 10,650 turns at about 21 ms each, a third of
  the coordinator's time, competing with claims.
- **Timeouts don't help the coordinator.** About 30% of registrations passed their 2s timeout
  (`RegisterSessionTimeout`), but most still ran: the workers made 11,486 attempts and 10,650
  reached the coordinator. A caller giving up doesn't take its call out of the queue.

Making each turn cheaper didn't raise throughput. Publishing and empty accepts took up the
capacity it freed.

## Proposal

Registration always goes through the reporter. The reporter sends as soon as a session gets work,
with at most one call in flight per queue.

1. **The session actor stops calling `RegisterSession`.** `AfterActorMethodAsync` keeps its in-process
   `Observe(queueId, sessionId, hasWork)`, and drops the direct call and `_registeredThisActivation`.
   An enqueue never waits on the coordinator.
2. **A session that gets work wakes the reporter.** `Observe` signals the queue's sender when it
   sees a session whose `HasWork` became true and that hasn't been sent as Active.
3. **One sender per queue per worker, with one call in flight:**
   - Woken and idle, it sends every pending Active change for the queue now, with any Empty changes
     already due.
   - Woken while a call is in flight, it does nothing. When the call returns, anything that
     arrived during it goes in the next call, immediately.

   At low load each new session is sent alone, at once, so a session is claimable about one
   coordinator call after its enqueue: the same as today, minus the enqueue's wait. Under load the
   batch grows with the backlog, so registrations cost one turn per batch rather than one per session.
4. **The 10 s tick stays** for what it does today: Empty changes after the 30s debounce, and a
   catch-all resend.
5. **Failed sends retry with backoff.** Start at 100 ms and double to 5s, instead of waiting for the
   tick. Nothing is marked sent until a call succeeds, as now.
6. **Batches are capped**, at 500 changes per call, so one turn stays short and claims get turns in
   between. A larger backlog goes in consecutive calls.
7. **Unknown sessions skip the lease read.** `MarkActiveAsync` reads `session-lock_{id}` to decide
   whether to queue a session. A session with no directory entry has normally never been claimed,
   so a batch of new sessions makes no lease reads. If one is in fact leased, nothing goes wrong: an
   any-available claim checks the lease at the head of the ReadyQueue, and drops a leased session
   there, as it does today.

Today's ordering guarantees hold:
- **A sender has one call in flight, so its reports for a queue arrive in order.**
- **The coordinator's deferral window still covers races between workers.** When a session actor
  moves between workers, the old worker's late Empty report is deferred.

### What callers see

- **The enqueue returns before its session is registered.** At low load the session becomes
  claimable one coordinator call later. Under load it's at most the call in flight plus its own call.
  That's below today's ~1s wait inside the enqueue.
- **An any-available accept in that window gets `NO_SESSIONS_AVAILABLE`.** The SDKs already back
  off and retry. HTTP callers that enqueue then accept straight away need to retry.
- **A targeted accept in that window would get `SESSION_NOT_FOUND`.** The coordinator doesn't know
  the session yet. This proposal closes that gap
  ([decision 3](#3-a-targeted-accept-for-a-session-not-yet-registered)): on a miss, the coordinator
  reads the session actor's state through the state API (no activation). If it has items, it adds the
  entry and claims it in the same turn.

## Expected effect

On the 10,000-session test:

- **Publishing stops waiting on the coordinator,** so the publish rate is set by the enqueue path
  alone.
- **Registration drops from about 10,650 turns to a few hundred batches.** Each costs one save (about
  25 ms at 10,000 sessions until the directory is segmented) plus a small amount per change. Its 33%
  share of coordinator time should fall to a few percent.
- **The limit moves to claims.** The coordinator's time goes to `ReleaseAndAcceptSession` and
  `AcceptSession`, which the segmented directory then makes cheaper.

How far throughput rises is for the measurement. If publishing is no longer the limit, the drain
should run at about the coordinator's claim rate.

## Not changing

- `RegisterSession` stays on `ISessionCoordinatorActor`, so a worker on the old build can still call it during a rolling upgrade.
- the reporter's Empty debounce, the coordinator's deferral window and the eviction pass
- leases, one save per turn, the scaler, the wire protocol and the SDKs

## Decisions

Weighed on blocking (what waits on what) and latency.

### 1. When the reporter sends

| Option | Blocking | Latency at low load | Under load | Other |
|---|---|---|---|---|
| a. On the tick only, shortened to ~250 ms | none | up to 250 ms added | one batch per tick | adds latency exactly when there's no load to save |
| b. Sync registration plus a breaker: after 2 failures in a row, enqueues skip the sync call for a while | the enqueue, until the breaker trips | none | the first calls of each burst each wait up to the 2s timeout, on every worker, before it trips | thresholds, open/half-open state, a recovery probe; the gateway handling the enqueue never sees the failures, so the worker has to hold the breaker |
| c. Sync, with at most K registrations in flight per queue per worker; the overflow goes to the reporter | the enqueue, for the first K | none | the overflow goes async at once | one counter. The first K still wait, and each turn still registers one session |
| **d. Always the reporter, sent at once, one call in flight per queue** | none | about one coordinator call, after the enqueue rather than inside it | batches grow with the backlog | one path, nothing to tune |

**Recommend d.** It's the only option where neither the enqueue nor the coordinator pays per
session under load. At low load it adds nothing: the call is made as soon as the session has work.

### 2. Batch cap

| Option | A turn at 10,000 sessions | Other |
|---|---|---|
| No cap | grows with the backlog. A burst of thousands is one long turn that claims wait behind | |
| **500 per call** | one save plus 500 directory updates, with no lease reads (proposal item 7) | consecutive calls clear a larger backlog |
| 50 per call | short | about 10× the turns of 500 under load |

**Recommend 500.** Revisit it once the directory is segmented and a save no longer grows with the
directory.

### 3. A targeted accept for a session not yet registered

| Option | Blocking | Latency | Other |
|---|---|---|---|
| a. Return `SESSION_NOT_FOUND` and let the caller retry | none | the caller's retry interval | a behaviour change for anyone who enqueues and then accepts a named session |
| **b. On a miss, read the session's state through the state API and claim it if it has items** | one state read inside the coordinator's turn, only on a miss | one read | the same read the eviction pass uses. The entry the report adds later finds it already Active |
| c. Make the enqueue flush the reporter and wait | the enqueue | one call | brings back the wait this proposal removes; **discounted** |

**Recommend b.**

### 4. Keeping the direct call as an option

| Option | Other |
|---|---|
| **a. Remove it** | one path |
| b. Keep it behind a setting, off by default | two paths to test, for a gain only at low load, which d already gives |

**Recommend a.**

## Tests (TDD order, per CLAUDE.md)

1. **Integration:**
   - publishing 2,000 new sessions completes while the coordinator is busy with claims, and no
     enqueue waits on it
   - an any-available accept retried after an enqueue claims the session within 1s
   - a targeted accept straight after an enqueue succeeds (decision 3)
2. **Session actor:** after an enqueue's first non-empty save, it calls `Observe` and makes no
   coordinator call.
3. **Reporter:**
   - a session that gets work is sent without waiting for the tick
   - while a call is in flight, further sessions wait, then go together in the next call
   - each queue has its own sender
   - a batch over 500 is split into consecutive calls
   - a failed send retries with backoff, not on the tick
   - Empty changes still wait for the debounce and go on the tick
4. **Coordinator:**
   - a batch of unknown sessions makes no lease reads
   - a targeted accept for an unregistered session with items reads its state, registers it and
     claims it in one turn
   - one with no items returns `SESSION_NOT_FOUND`

## Measuring it

r20 with the timing build, before and after:

- **The workload this targets.** 100k messages over 10,000 sessions. Compare publish time,
  throughput, `ReportSessionChanges` batch sizes and turns, and coordinator busy time.
- **No regression.** 100k messages over 1,000 sessions, comparing throughput and the time from first
  enqueue to first claim.
- **Low load.** One publisher, one consumer, 100 sessions. Compare the time from a session's first
  enqueue to its claim, against today's direct registration.

## Related work

- [session-coordinator-sharding.md](session-coordinator-sharding.md): the measurements, and phase 1b.
- [session-directory-segments.md](session-directory-segments.md): the next item, which makes every
  coordinator save cheaper, batches included.
- [session-directory.md](session-directory.md): the reporter, the deferral window and why
  registration was made direct in the first place.
