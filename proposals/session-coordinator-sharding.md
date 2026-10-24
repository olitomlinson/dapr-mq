# Sharding the session coordinator

> **Status: phase 1 built (PR #103, 2026-10-24); phase 2 proposed, not yet needed.** Phase 1 raised
> r20 session throughput on kind by about 27%, and the coordinator is now busy about a third of the
> drain (see [Phase 1 results](#phase-1-results)). Phase 2 is for workloads that make it the limit
> again: short sessions, or many more consumers. All numbers are from kind (a laptop). Confirm them
> with repeats in CI (`perf-extreme`, `k8s_repeats` ≥ 3) before phase 2 starts.

## Context

Every session of a queue goes through one `SessionCoordinatorActor`, whose id is the queue id. One
actor runs one call at a time (reentrancy is off, [ADR 0003](../docs/ADR/0003-disable-actor-reentrancy.md)),
so every one of these waits for the same turn:

| Call | Caller | Per session |
|---|---|---|
| `RegisterSession` | the session's `QueueActor`, on its first call with work in each activation | about 1.7 |
| `ReportSessionChanges` | each worker's `SessionActivityReporter`, batched | negligible |
| `AcceptSession` | the gateway, for `ConsumeSession` and the HTTP accept endpoint | 1 that claims, about 1.1 that find nothing ready |
| `RenewSessionLease` | the gateway, every `leaseSeconds / 2` of a session's life | about 1.6 at a 30s lease |
| `ReleaseSession` | the gateway, when a session's stream ends | 1 |

That's about **6.5 coordinator turns per session**.

### What the throughput test measured

`tools/k8s-throughput --workload sessions`, 20 replicas × 10 sessions, 100k messages over 1,000
sessions, prefetch 1, lease 30s, 200ms of work per message, Postgres at 2 CPU. The coordinator was timed per
method on a diagnostic build: the gateway timed each call end to end, and `OnPre/OnPostActorMethodAsync`
timed the work inside the turn. 429 msg/s.

| Call | Gateway waits | Inside the turn |
|---|---|---|
| `AcceptSession` (claiming) | 812 ms (all accepts) | 46 ms |
| `RenewSessionLease` | 1,659 ms | 42 ms |
| `ReleaseSession` | 1,408 ms | 47 ms |
| `RegisterSession` (from the session actor) | 522 ms | 3 ms |

- **The waits are queueing.** The coordinator ran calls for 171s of the ~210s drain. The rest of each
  wait is time spent behind other calls.
- **The work inside a turn is mostly waiting on something else:**
  - the lease sync to the session actor: 14–16 ms
  - state saves: 6–19 ms
  - scheduler reminder calls: 7–12 ms each

  None of it is CPU.
- **The waits reach other actors.** A session actor makes its `RegisterSession` call inside its own turn
  (bounded by `RegisterSessionTimeout`, 2s), so a slow coordinator also stalls that session's enqueues.
  A consumer's renewal runs inside `ConsumeSession`'s delivery loop, so the stream delivers nothing
  while it waits.

### Cutting the cost per call didn't help

Making renewals stop re-registering the lease reminder (the reminder re-arms itself when it fires
on a renewed lease) cut a renewal from 42 to 23 ms and halved the scheduler calls. Coordinator busy
time fell 7%, and throughput didn't move (430 msg/s, against 420–456 for three runs without it). The work
left in each turn is the lease sync and the saves, which the coordinator must still do.

### The ceiling

One coordinator runs at most about 1 / 26 ms ≈ **38 turns/s** (26 ms is the mean time inside a turn
across all calls). So:

```
ceiling (msg/s) ≈ shards × turns per second ÷ turns per session × messages per session
                ≈ 1 × 38 ÷ 6.5 × 100
                ≈ 580 msg/s
```

That matches every r20 result so far: 420–533 msg/s, and 581 with `--fast-scale-up`. Adding replicas,
Postgres CPU or publish jitter doesn't move it. For a queue whose sessions are shorter, the ceiling
falls in proportion. At 10 messages per session it's about 58 msg/s.

## Two levers

The ceiling has two terms we control. They multiply.

| Lever | What changes | Ceiling gain | Cost |
|---|---|---|---|
| **Fewer turns per session** | Combine release with the next accept; stop accepts that find nothing; renew less often | 6.5 → about 3 turns: **about 2×** | Small. Gateway and coordinator only, no state change |
| **More coordinators (sharding)** | N coordinators per queue, each owning the sessions that hash to it | **N×**, until Postgres or the workers saturate | Medium. Routing, any-available accept, readers, shard count |

Reducing turns is bounded: a session needs at least one claim and one end. Sharding is the only
lever without a fixed limit, but every shard shares Postgres, the scheduler and the worker pods. The
sharded ceiling holds only until those saturate, and the per-turn I/O latency above will rise as
they load up.

## Proposal

Do both, cheapest first. Phase 1 makes every shard of phase 2 cheaper too.

### Phase 1: fewer turns per session

1. **Release and accept in one call** (built). `SessionCoordinatorActor.ReleaseAndAcceptSession`
   ends one lease and claims any available session in one turn.

   A `ConsumeSession` stream handles one session, so a release and the next claim arrive on
   different streams. The gateway's `SessionReleaseHandoff` joins them:
   - When an any-available stream drains, its lease is parked for up to
     `DAPRMQ_SESSION_RELEASE_HANDOFF_MS` (default 500 ms).
   - The consumer opens its next stream straight away. When that stream starts on the same gateway,
     it takes the parked lease and sends the combined call.
   - A lease nobody takes is released on its own.
   - Targeted streams, and streams that end any other way, release straight away.

   One turn saved per session. On a sharded queue the combined call goes to the released session's
   shard, so its claim can only come from that shard. That makes streams stick to a shard, which
   phase 2 caps ([decision 5](#5-how-long-a-stream-stays-on-one-shard)).
2. **Stop accepts that find nothing** (dropped). The run's timing logs put all 1,129 of these after
   the last session was claimed, from streams polling while the final sessions finished. None
   happened during the drain, so stopping them can't raise the ceiling. Their cost on a sharded
   queue is covered in [decision 2](#2-how-an-any-available-accept-finds-a-shard).
3. **Renew less often** (measured, not doing). At a 60s lease no session needed a renewal, but
   throughput didn't move (575 msg/s, against 524–580 at 30s): renewals had already become cheap
   (item 4). Doubling the time before a crashed consumer's session is taken over isn't worth it.
   Moving renewals off the coordinator entirely is
   [session-lease-on-session-actor.md](session-lease-on-session-actor.md), which stays deferred.
4. **Renewals leave the lease reminder alone** (built). The reminder re-arms itself for the time left
   when it fires on a renewed lease. A renewal drops from 42 ms to 9–13 ms, and scheduler calls halve.

### Phase 1 results

Same test and diagnostic build as above, on kind. All runs at r20.

| | Before (3 runs) | Phase 1 (2 runs) | Phase 1, lease 60s |
|---|---|---|---|
| Throughput | 420, 456, 429 msg/s | **524, 580 msg/s** | 575 msg/s |
| Coordinator busy | 171 s | 88 s, 55 s | 52 s |
| Gateway waits per renewal | 1,659 ms | 733 ms, 314 ms | no renewals |
| Gateway waits to release and claim the next | 1,408 + 812 ms (two calls) | 824 ms, 561 ms (one call) | 740 ms |

- **Every release went through the combined call.** All 1,000 used `ReleaseAndAcceptSession`.
- **Turns are cheaper.** The coordinator's turns now average about 11 ms, against 26 ms before. About
  3.9 turns per session happen during the drain:
  - 1 combined release and claim
  - 1 renewal
  - about 1.7 `RegisterSession`
  - the stream's first claim
- **The ceiling formula now gives roughly 88 turns/s ÷ 3.9 × 100 ≈ 2,200 msg/s** at 100 messages
  per session. That's well above what 20 replicas reach, so the coordinator isn't the limit at
  this scale. Per-turn cost rises with load, so treat it as an upper bound.
- **The waits are now bursts.** The gateway still waits 0.6–0.7 s per combined call against about
  40 ms inside the turn. At a third utilisation that isn't steady queueing. Equal-length sessions
  finish together, and about 200 combined calls arrive at once, as with the waves that
  `--publish-jitter` smooths.
- **The next limits are elsewhere.** At efficiency 0.58 they are the KEDA ramp (`--fast-scale-up`),
  the tail, and the per-message round trip at prefetch 1.

### Phase 2: shard the coordinator

N coordinators per queue. A session belongs to shard `FNV-1a(sessionId) mod N`, the same hash as
`CatalogShards`. Each shard is today's coordinator over a slice of the sessions: its own
directory, `ReadyQueue`, `dir_summary`, `session-lock_*` keys, lease reminders and eviction pass.

#### Routing

| Call | Knows its session | Routed by |
|---|---|---|
| `RegisterSession` | yes | the session actor, from its own session id |
| `ReportSessionChanges` | yes, per change | the reporter, which groups each batch by shard (one call per shard with changes) |
| `AcceptSession` for a named session | yes | the gateway |
| `RenewSessionLease`, `ReleaseSession` | yes, the request carries `SessionId` | the gateway |
| `AcceptSession`, any available | **no** | see [decision 2](#2-how-an-any-available-accept-finds-a-shard) |

Routing is done in the gateway and the worker. SDKs, the gRPC and HTTP contracts, and lease ids don't
change.

#### Readers

| Reader | Today | Sharded |
|---|---|---|
| KEDA scaler, `QueueDepthService` (`NonEmptySessions`) | reads one `dir_summary` | reads N and sums `ActiveCount` (N small state reads, no actor calls) |
| Console session list (`ReadSessionIdsAsync`) | reads one `metadata` | reads N and merges, sorted by id |
| Catalog registration | the coordinator registers its queue on activation | shard 0 only |

#### What doesn't change

- Session actors, `SetSessionLease`/`ClearSessionLease`, and the rule that the session actor agrees
  to a lease before the coordinator reports it.
- The session actor id, `{queueId}-session-{sessionId}`.
- Shard 0's id is `{queueId}`, today's id. With N = 1 a sharded build behaves exactly as today, and needs no
  migration.

#### Ordering

Today an any-available accept hands out sessions in the order they got work, across the whole
queue. Sharded, that order holds within each shard only. Across shards it's approximate,
depending on which shard each accept tries first. Messages within a session stay strictly in order:
a session lives in one shard, and its messages live in its own actor.

## Decisions

Weighed on blocking (what waits on what) and latency.

### 1. How the shard count is set

| Option | Blocking | Latency | Other |
|---|---|---|---|
| **a. One deployment-wide setting**, `DAPRMQ_SESSION_COORDINATOR_SHARDS`, default 1 | none | none: routing is a hash | Every session queue gets the same N. Changing it re-homes sessions (see below) |
| b. Per queue, recorded by shard 0 when the queue's first session registers; gateways cache it | a cold cache costs one state read | one read per queue per gateway start | Hot queues can be sharded alone. Still fixed once set |
| c. Resizable, with re-homing | moving a session holds two shards | a re-home pass per resize | Most flexible, by far the most code |

**Recommend a**, the catalog's approach. Changing N moves sessions to new shards. An Idle entry
left in its old shard is evicted by that shard's normal pass. But a session that still has items,
left in a shard that's no longer polled, would never be offered. So the setting's docs must say:
change it only with the session queues drained. A re-home pass (option c) can follow if that proves
too strict.

### 2. How an any-available accept finds a shard

| Option | Blocking | Latency | Discounted? |
|---|---|---|---|
| **a. Random start, then probe in order** until a claim or all N say none | each probe is a turn on that shard | 1 turn when sessions are ready; up to N when the queue is nearly empty | no |
| b. Read every shard's `dir_summary` first and go to one with ready sessions | none on actors: state reads | N state reads per accept, or a ~1s cache | no, see below |
| c. A home shard per stream (stream i → shard i mod N) | none | 1 turn | **yes**: a shard with fewer streams than ready sessions backs up while others idle, and with fewer streams than shards some sessions are never offered |
| d. Accept from all shards in parallel, keep the first, release the rest | N turns per accept, always | 1 round trip | **yes**: multiplies turns, the thing being saved |

**Recommend a.** Probes cost turns only when the queue is nearly empty, and that's when the shards
have turns to spare. While sessions are queued the first probe almost always claims. The cost
that remains is idle streams polling an empty queue, which becomes N× today's. The SDKs already
back off after a failed claim (doubling from 1s to 60s), which bounds it. If it still measures
badly, add a short per-queue backoff in the gateway after `NO_SESSIONS_AVAILABLE`.

Option b needs a `ReadyCount` in `dir_summary`. Today the summary is written only when a session
changes between Active and Idle, so keeping a ready count current would add a state write to every
claim turn. Revisit b only if probing measures badly.

### 3. The id of shards 1 to N−1

| Option | Collision risk | Other |
|---|---|---|
| **a. `{queueId}-coord-{n}`** | a queue literally named `orders-coord-1` would share an actor with shard 1 of `orders` | readable in logs and the state table |
| b. a separator that queue ids can't contain | none, but queue ids aren't validated today, so any character could already be in use | needs a new format rule, and possibly a breaking one |

**Recommend a**, with the controller rejecting new queue ids that end in `-coord-` and digits,
as format validation. Coordinators are their own actor type, so ids can only collide with other
coordinators, never with a `QueueActor`.

### 4. The default N

**Recommend 1**: nothing changes until an operator opts in. The ceiling formula tells them when:
shards ≈ target msg/s × 6.5 (or about 3.5 after phase 1) ÷ messages per session ÷ 38.

### 5. How long a stream stays on one shard

`ReleaseAndAcceptSession` is one turn on one actor: the shard that holds the released session. So
its claim comes from that shard, and a stream that uses it every time drains its shard until the
shard has nothing ready. Only then does the stream fall back to probing (decision 2a).

- **The benefit:** no probes while the shard has ready sessions.
- **The risk:**
  - With fewer streams than shards, or an uneven spread, a shard with no stream on it is served only
    when some other shard runs dry.
  - Under steady publishing the other shards may never run dry, so that shard's sessions wait
    indefinitely. That's the flaw of home shards (2c), reached another way.
  - Order across shards also skews further: one shard's sessions go ahead of older ones elsewhere.

| Option | Blocking | Latency | Turns saved per session | Discounted? |
|---|---|---|---|---|
| a. Always combine | a shard with no stream waits for another shard to empty, unbounded under steady load | none | 1 | **yes**: starvation |
| b. Combine at most K times in a row, then a plain `ReleaseSession` and a probe from a **random** shard | no upper limit on a shard's wait. With one stream and N = 4, a shard is still unvisited after 8 moves about 1 time in 10 | none | (K−1)/K: 0.75 at K = 4 | **yes**: the wait is only probably short, and few streams (where starvation is a risk) is where it's widest |
| **c. Combine at most K times in a row, then a plain `ReleaseSession` and a probe from the next shard** (shard + 1, + 2, …, wrapping round to its own shard last) | each stream has visited every shard by its ((N−1) × K + 1)-th session: the 13th at N = 4, K = 4 | none | (K−1)/K: 0.75 at K = 4 | no |
| d. Pick the next shard at random; combine only when it's the released session's shard | none | none | 1/N | **yes**: fair, but keeps a quarter of the saving at N = 4 |
| e. Combine only when the released session's shard has more ready sessions than its share | none | a summary read per refill | about 1 | **yes**: needs `ReadyCount` in `dir_summary` (decision 2b's extra write per claim) |

**Recommend c, with K = 4.**

- **The limit holds with any number of streams, even one.** It doesn't depend on a random choice landing well.
- **Most of the saving remains.**
- **No state is added.** The gateway keeps the count and the current shard per stream.
- **With N = 1 the cap is skipped.** There is no other shard, so every refill combines.

Rotating has one weakness. Streams on the same shard move to the next one together and stay grouped,
where random moves would spread them out. So the two moves are kept separate:

- **Reaching the cap rotates.** That gives the limit above.
- **Everything else starts at a random shard (decision 2a):** a new stream's first accept, and a
  stream whose shard ran out. That's where streams would otherwise group, so randomness spreads them
  out there.

## Alternatives considered

| Alternative | Why not (now) |
|---|---|
| Only phase 1 | It's bounded at about 2×; a queue of short sessions stays capped at ~100 msg/s |
| Only sharding | It works, but every shard still spends half its turns on empty accepts and renewals, and empty accepts multiply by N |
| Shorter turns (move the reminder, the saves or the sync out of the turn) | Measured: −7% busy time, no throughput change. The sync and the saves are what make a claim safe |
| Leases on the session actor ([proposal](session-lease-on-session-actor.md)) | Removes renewals and lease ends from the coordinator (about 2.6 turns per session). Larger change, and still one coordinator for claims. Compatible with sharding, and the natural phase 3 |
| Turn reentrancy back on | Superseded by ADR 0003. It removed the turn as a limit only by risking interleaved state writes |

## Tests (TDD order, per CLAUDE.md)

Phase 1 (built, PR #103):

1. Integration: two any-available streams in turn each get the next session, and both drained
   sessions are released within the handoff window.
2. Gateway (`ConsumeSession`):
   - a drained any-available stream's next stream makes one combined call
   - targeted and client-ended streams release immediately
   - a failed combined call releases the lease it took
3. Handoff:
   - a parked lease is taken once, oldest first, for its own queue only
   - an untaken lease is released after the window, and never once taken
   - stopping releases all
   - a zero window parks nothing
4. Coordinator:
   - `ReleaseAndAcceptSession` releases and claims in one turn
   - a rejected release still claims
   - nothing ready still releases
   - a renewal leaves the reminder alone
   - a reminder firing on a renewed lease re-arms and keeps it

Phase 2:

1. Integration: a queue on 4 shards drains with no FIFO violations and no session handed to two
   consumers; `NonEmptySessions` equals the sum over shards.
2. Routing: a session's id always maps to the same shard; N = 1 sends everything to `{queueId}`.
3. Gateway: an any-available accept probes from a random shard, stops at the first claim, and returns
   `NO_SESSIONS_AVAILABLE` only after all N say none.
4. Gateway: a stream refills with `ReleaseAndAcceptSession` at most K times in a row on one shard, then
   releases plainly and probes from the next shard in turn. A single stream on N = 4 with every shard
   ready has claimed from all four by its 13th session. A stream whose shard is empty probes from a random
   shard. With N = 1 it always combines.
5. Reporter: one batch with changes in three shards makes three calls, each with only its own changes.
6. Readers: the scaler sums and the console merges across shards; a missing shard reads as empty.
7. Validation: a queue id ending in `-coord-<digits>` is rejected with 400.

## Measuring it

Phase 1 is measured above.

Before phase 2, find a workload where the coordinator is the limit again. Either:

- short sessions: 100k messages over 10,000 sessions of 10, or
- more consumers than r20

Run it with `--repeats 3` on kind and in `perf-extreme`. Start phase 2 if the coordinator is busy for
most of the drain (the `metrics.diagnostics` latency rising with replicas). Then compare N = 1, 2
and 4. Expect gains until Postgres or the workers become the limit. Finding out which one is part
of the measurement.

## Related work

- [session-directory.md](session-directory.md): the directory, `ReadyQueue` and eviction pass each
  shard keeps.
- [session-lease-on-session-actor.md](session-lease-on-session-actor.md): deferred; listed sharding as
  its phase 4.
- `CatalogShards` ([CatalogModels.cs](../server/src/DaprMQ.Interfaces/CatalogModels.cs)): the hash
  and the merge-on-read pattern reused here.
- PR #103: phase 1 (the combined call, the handoff and the reminder change).
