# A segmented session directory

> **Status: built (2026-10-25)**, as the detailed design for phase 1b, item 1, of
> [session-coordinator-sharding.md](session-coordinator-sharding.md). Measured on kind (a laptop):
> 10,000 sessions of 10 went from 171 to 395–453 msg/s. See [Results](#results).

## Context

A `SessionCoordinatorActor` keeps its whole directory in one state key:

| Key | Holds |
|---|---|
| `metadata` | `SessionDirectory`: one `SessionEntry` per known session, about 100 bytes each. `ReadyQueue`: the ids waiting to be claimed |
| `dir_summary` | `TotalCount`, `ActiveCount`: the only key the KEDA scaler reads |
| `session-lock_{id}` | one lease |

Every claim, release, registration and eviction change rewrites `metadata` whole, along with
everything in it. [session-directory.md](session-directory.md#why-the-directory-isnt-segmented)
chose this on purpose. Eviction keeps the directory at roughly "sessions with work, plus those
created in the last `Grace`" (10 minutes). That proposal named the case where the choice stops
holding: "thousands of sessions holding work at the same time, combined with a high claim rate".

The short-session test is that case. 100k messages over 10,000 sessions of 10, at r20:

| | 1,000 sessions | 10,000 sessions |
|---|---|---|
| `RegisterSession`, inside the turn | 3 ms | 21 ms |
| A claim's save | 15 ms | 25 ms |
| A combined release and claim | 38–64 ms (two saves) | 33 ms (one save) |
| Coordinator busy | about a third of the drain | 98% |
| Throughput | 524–580 msg/s | 141 msg/s, limited by publishing |

At 10,000 sessions, saves are 75–85% of the time inside each call. Three costs grow with the
directory:

- **Postgres writes.** About 1 MB per save at 10,000 sessions, whatever the change.
- **CPU.** Every change copies the directory (`new Dictionary<,>(...)`, then `WithEntry`). Every save
  serialises all of it, on workers with a 0.5 CPU limit.
- **A hard ceiling.** daprd limits request bodies to 4 MB by default
  ([Dapr docs](https://docs.dapr.io/operations/configuration/increase-request-size/)). At about 100
  bytes an entry, a save would fail at around 40,000 sessions in one directory. That includes Idle
  entries still within `Grace`.

## Proposal

Store the directory as fixed hash buckets, and the ReadyQueue as a segmented FIFO. A turn writes
only the keys it changed, and still saves once at the end of the turn (CLAUDE.md, "Atomic by
default"). A Dapr actor state transaction covers every key of the actor, so a turn that touches
several keys still commits as one transaction.

### Layout

| Key | Holds | Size at 10,000 sessions |
|---|---|---|
| `dir_{n}`, n = 0…63 | the `SessionEntry`s whose session hashes to n (`FNV-1a(sessionId) mod 64`, as `CatalogShards` does) | ~156 entries, ~16 KB |
| `ready_{k}` | up to 100 ready items, oldest first | ~4.5 KB |
| `ready_meta` | `Head`, `Tail` (segment numbers), `NextSeq` | a few bytes |
| `dir_summary` | unchanged | unchanged |
| `session-lock_{id}` | unchanged | unchanged |

A ready item is `(SessionId, Seq)`. `SessionEntry.Queued` (a bool) becomes `ReadySeq` (a nullable
long): the sequence number of the session's one live item in the ReadyQueue. "Queued" now means
`ReadySeq != null`.

### Stale items stay where they are

Today a session leaves the ReadyQueue through `RemoveFromReady` (a claim at the head, or a stale
head being dropped) or the eviction pass's `ready.Remove`. Both search the whole list, and eviction
can take an id from the middle. In a segmented FIFO that means finding and rewriting whichever
segment holds it. Instead:

- **Removing a session from the ReadyQueue only clears `entry.ReadySeq`.** The item stays.
- **Queueing a session appends a new item** with the next `Seq`, and sets `entry.ReadySeq` to match.
- **An item is live only if `entry.ReadySeq == item.Seq`.** Otherwise it is stale: the session was
  claimed, evicted or queued again since.

An any-available claim pops items from the head:
- **Stale items are dropped.** Checking one needs only its bucket (cached after the first read) and
  no lease read.
- **Live items are tried as today:** dropped if Idle or leased, claimed otherwise.

Stale drops don't count toward `MaxClaimAttempts` (16). They have a cap of their own, about 500 per
turn, so a long run of stale items can't stretch one turn without limit. Each turn that drops items
also rewrites the segments it emptied, so the queue shrinks as it goes.

This keeps today's rule that "ids can go stale and claims drop them at the head", and makes it
exact. Leaving items in place means a session can have an old item as well as its current one. The
`Seq` check is what tells them apart.

### The directory layer

A `SessionDirectory` class, created per turn over the actor's `StateManager`, replaces direct use
of `metadata`:

| Member | Reads | Stages |
|---|---|---|
| `GetEntryAsync(id)` / `SetEntry(id, entry)` / `RemoveEntry(id)` | the entry's bucket, once per activation (the `StateManager` caches it) | that bucket |
| `EnqueueReady(id)` | `ready_meta` and the tail segment | the tail segment (a new one when full), `ready_meta`, the entry's bucket |
| `PeekReadyAsync()` / `PopReady()` | the head segment | the head segment, plus `ready_meta` when the head moves on |
| `AllEntriesAsync()` | all 64 buckets (cached after the first eviction pass) | nothing |
| `Summary` | `dir_summary` | `dir_summary` |

- **Changes are tracked per key.** The layer stages a bucket or segment only if the turn changed
  it, and never saves. The actor method's single `SaveStateAsync()` commits everything.
- **The rest of the coordinator keeps its shape.** `ApplyChangesAsync`, `ClaimAsync`,
  `ApplyLeaseEndAsync` and the eviction pass move onto this layer, but their logic doesn't change.
  All 16 current uses of `SessionDirectory` go through it.

### What each turn writes

| Turn | Today | Segmented |
|---|---|---|
| Claim | `metadata` (whole) + lock | head segment (+ `ready_meta`) + the claimed session's bucket + lock |
| Release, nothing left | `metadata` + `dir_summary` + lock | the session's bucket + `dir_summary` + lock |
| Release, items left | `metadata` + `dir_summary` + lock | the session's bucket + tail segment + `ready_meta` + `dir_summary` + lock |
| Combined release and claim | `metadata` + `dir_summary` + 2 locks | the union: about 5–7 keys, each ≤ 16 KB |
| Registration (`ReportSessionChanges`) | `metadata` + `dir_summary` | each new session's bucket + tail segment + `ready_meta` + `dir_summary`, for the whole batch |
| Eviction pass | `metadata` + `dir_summary` | only the buckets with changed entries, + any segments appended to, + `dir_summary` |

### Activation

- **Today, activation reads the whole directory** to rebuild `_lastActiveAt`, recording "Active, as
  of now" for every Active entry, and to decide whether to register the eviction reminder.
- **Segmented, activation reads only `dir_summary`.**
  - **`_lastActiveAt` falls back to the activation time** for an entry it has no record of. That
    gives the same "as of now" result without reading the entries.
  - **The eviction reminder is registered when `TotalCount > 0`.**

### External readers

- **The KEDA scaler is unchanged.** It reads `dir_summary`.
- **The console's session list** (`ReadSessionDirectoryAsync`, via `AdminReadService`) reads the 64
  buckets in parallel through the state API, without activating the actor, and merges them sorted
  by id. That's 64 small reads instead of one large one, and only when someone opens the page.

### Existing state

None is converted or read. DaprMQ hasn't reached v1, so there's no compatibility to keep with
existing actors: an old `metadata` key is simply ignored, and coordinators start from the new
layout. See [decision 4](#4-existing-state).

## Expected effect

- **Saves.** A save writes 3–7 small keys instead of one ~1 MB key. In the 10,000-session runs, a
  transaction that only deleted a lock took 1.3 ms. So I'd estimate 2–5 ms per save, against 12–25 ms,
  and a combined call of about 10 ms against 33 ms. Measure it.
- **CPU.** No whole-directory copies or serialisation per change.
- **The ceiling.** None from the directory. A bucket reaches 4 MB only at about 2.5 million
  sessions.
- **Reads.** At most 64 bucket reads and a few segment reads per activation, each cached after.
- **Small directories.** A few hundred sessions spread over 64 buckets means each save writes a
  few hundred bytes instead of tens of KB. That's no worse than today, and still one transaction.

This doesn't change the coordinator's single turn, or the wait in `RegisterSession` while enqueuing.
Those are phase 1b item 2 and phase 2 of the sharding proposal. With saves cheap, the 10-per-session
test should show how much of its limit is turn count rather than turn cost.

## Where it's slower

| Scenario | Why | How much | Mitigation |
|---|---|---|---|
| **The first calls after the coordinator activates.** A worker restarts, placement moves the actor, or it reactivates after the 60-minute idle timeout | Today one read loads the whole directory. Segmented, each bucket and segment is read the first time a turn touches it. A claim reads `dir_summary`, `ready_meta`, the head segment and a bucket: 4 reads, not 1. A claim dropping stale items across many buckets, or the first eviction pass, can read up to 64 buckets one after another in one turn | tens of ms for most first calls; at worst about 64 × 1–3 ms ≈ 100–200 ms in one turn, against the eviction pass's 250 ms budget | read every bucket at activation only when `TotalCount` is large, or let the first pass stop early and finish on the catch-up reminder (it already can) |
| **Very small directories,** tens to a few hundred sessions | today's blob is already tiny, so the cost of each extra row dominates. A claim writes 3–4 keys instead of 2 | probably under 1 ms per save; about break-even at a few hundred sessions, better above | none; confirm in the 1,000-session check below |
| **An eviction pass that changes entries in many buckets at once,** such as marking hundreds Idle | it rewrites every bucket it touched. If that's most of them, it writes about today's blob size, spread over up to 64 rows | the same as today or slightly worse, for that pass. Passes are bounded (≤ 64 reads, 250 ms) and run every 30 s | none |
| **The console's session list** | 64 parallel state reads and a merge instead of one read | tens of ms more per page view, and 64 requests to daprd | only when the page is opened; page through buckets if needed |

The first row is the one that matters. Placement moves the coordinator when workers scale or
restart, which tends to happen under load. The others are small, rare or one-off.

These don't get worse:
- **Stale items.** Today stale ids are also dropped at the head, and count toward the 16 claim
  attempts. Here they don't count.
- **Bytes per combined call.** Even when its release and its claim touch different buckets, it
  writes about 32 KB, against 1 MB today at 10,000 sessions.

## Not changing

- leases, `session-lock_{id}`, `SetSessionLease`/`ClearSessionLease`, and the rule that a session
  actor accepts a lease before the coordinator records it
- one save per turn
- the scaler's read, the wire protocol and the SDKs
- eviction's rules, budget (`MaxPassDuration`, `MaxReadsPerPass`) and timings

## Decisions

Weighed on blocking (what waits on what) and latency.

### 1. Bucket count

| Option | Write per change at 10k / 100k sessions | Reads after activation | Other |
|---|---|---|---|
| **a. Fixed 64** | ~16 KB / ~160 KB | ≤ 64 | the catalog's choice |
| b. Fixed 256 | ~4 KB / ~40 KB | ≤ 256 | small directories spread very thin. The eviction pass's first read after activation is 256 requests, which could exceed its 250 ms budget on a cold cache |
| c. Dynamic (split a bucket that grows past a size) | ~16 KB always | grows with the directory | every reader has to discover the bucket layout; **discounted** as complexity with no measured need |

**Recommend a.** 160 KB per change at 100,000 sessions is still about 6× less than a 10,000-session
blob today. The count is a constant: changing it later moves sessions to other buckets, which before v1 is
handled like any other layout change ([decision 4](#4-existing-state)).

### 2. The ReadyQueue's shape

| Option | Write per claim at 2,700 ready | Other |
|---|---|---|
| a. One key of ids | ~120 KB | simplest; still grows with the backlog, and the 10,000-session test had ~2,700 ready at once |
| **b. Segmented FIFO, 100 per segment** | ~4.5 KB | the queue actor's own pattern (CLAUDE.md) |
| c. No queue: scan buckets for Active, unleased entries | none | loses the order sessions became ready in, and a claim scans every bucket; **discounted** |

**Recommend b.**

### 3. How a session leaves the ReadyQueue

| Option | Blocking | Latency | Other |
|---|---|---|---|
| a. Remove its item from whichever segment holds it | finding it reads segments in turn | up to every segment, inside the turn | **discounted**: the cost grows with the queue |
| **b. Leave the item, mark it stale by `Seq`** | none | claims drop stale items at the head (bucket lookups, no lease reads) | exact; adds 8 bytes per entry and per item |
| c. Leave the item, judge it by today's `Queued` flag | none | as b | **discounted**: when a session has an old item ahead of its current one, the flag can't tell them apart, so the old one is taken as live and the session jumps the queue |

**Recommend b.**

### 4. Existing state

| Option | Risk | Cost |
|---|---|---|
| **a. No conversion: start each coordinator empty**, as [session-directory.md](session-directory.md#no-migration) did | queues upgraded mid-flight lose their directory. Sessions with work can't be claimed until each is reported again, and an idle reporter sends nothing | none |
| b. Convert on activation, then drop the code after one release | one turn with a large read and one save per coordinator | ~40 lines, a test, and a reader fallback |

**Decided a (2026-10-25).** DaprMQ hasn't reached v1, so the project keeps no compatibility with
existing actors or state. b was built first and then removed. Upgrading a queue mid-flight before v1
means draining it first, or accepting that its sessions are re-registered only as they get new work.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Split `metadata` into two keys (directory, ReadyQueue) | A claim still rewrites the whole directory to clear `Queued`, so it removes only part of the cost |
| One key per entry (`dir_{sessionId}`) | The smallest writes, but eviction and the console need a list of every id. That list is itself a growing key, or a segmented one, which brings back buckets |
| Raise daprd's `max-body-size` | Moves the ceiling; the cost per save still grows with the directory |
| Rely on sharding | Each of N shards still writes its whole slice. That divides the cost by N but doesn't stop it growing |

## Tests (TDD order, per CLAUDE.md)

1. **Integration:** 2,000 sessions published and drained with no FIFO violations. After the drain,
   the console's session list returns all 2,000 merged across buckets (HTTP contract unchanged).
2. **Directory layer, with a mocked state manager:**
   - an entry round-trips through its bucket
   - a turn stages only the keys it changed
   - `EnqueueReady` fills a segment, then starts a new one
   - `PopReady` moves the head on, and stages `ready_meta`
3. **Stale items:**
   - a claimed, evicted or requeued session's old item is dropped at the head without a lease read
   - only the item matching `ReadySeq` is claimed
   - stale drops don't count toward `MaxClaimAttempts`, and stop at their own cap
4. **Coordinator:**
   - every existing coordinator test passes unchanged, apart from the asserts on the state's shape
   - a claim writes no other bucket
   - each turn still saves once
5. **Activation:**
   - activation reads only `dir_summary`
   - an entry with no `_lastActiveAt` defers an Empty report as if marked Active at activation
6. **Reader:** `ReadSessionDirectoryAsync` merges 64 buckets and treats a missing bucket as empty.

## Results

r20, prefetch 1, lease 30 s, with each coordinator call timed. Before is `main` after
[async registration](async-session-registration.md), which registers every session up front, so
every save writes the whole directory from the start.

| | Before (one blob) | Segmented |
|---|---|---|
| 10,000 sessions of 10: throughput | 171 msg/s | **395, 453 msg/s** (two runs) |
| Combined release and claim, inside the turn | 56 ms | 21.5 ms |
| Its save | 44 ms | 5.1 ms |
| Gateway's wait per combined call | 8.1 s | 1.4 s |
| 1,000 sessions of 100: throughput | 576 msg/s | 588 msg/s |
| 1,000 sessions: the combined call's save | 11.4 ms | 4.7 ms |

At 10,000 sessions the coordinator is still close to saturated: combined calls took 215 s of the
253 s run. What's left of each 21.5 ms is mostly calls out of the turn:
- registering the lease reminder (4.2 ms)
- the lease sync to the new session's actor (3.7 ms)
- clearing the old session's lease (3.4 ms)
- unregistering the old reminder (a few ms)
- the save (5.1 ms)

Of those, the lease reminders are the cheapest to remove: a lapsed lease could be left to the
eviction pass, at the cost of up to 30 s before its session is offered again. Sharding is what
scales past a single coordinator's turn rate.

**Activation cost.** 120 s into a 10,000-session drain, the worker pod hosting the coordinator was
deleted. The coordinator reactivated on the other worker, then moved again when the replacement pod
joined.
- **Claims resumed within about 10 s.** For the next 10 s, combined calls averaged 44.6 ms (max
  421 ms) while buckets were read cold, then settled at about 20–25 ms.
- **The first eviction pass took 380 ms in one turn,** over its 250 ms budget, because it reads all 64
  buckets cold. The catch-up passes that followed averaged 25 ms. That's acceptable as a one-off per
  activation, so no mitigation is needed yet.
- **The run drained fully.** All 100,000 messages, 0 FIFO violations, and 7 redeliveries: messages
  in flight on the deleted worker, delivered at least once as designed. Throughput for that run was
  274 msg/s: half the session actors also moved, and their in-flight leases had to lapse.

## Measuring it

- **First, confirm the ceiling.** On `main`, register 50,000 sessions on one queue and see whether
  coordinator saves fail at about 4 MB. If they do, record it in this proposal.
- **The workload this targets.** r20, 100k messages over 10,000 sessions, before and after, with the
  timing build. Compare save time per call, coordinator busy time and throughput.
- **No regression.** r20, 100k messages over 1,000 sessions: throughput and save time no worse.
- **Activation cost.** At 10,000 sessions, restart the worker hosting the coordinator mid-drain.
  Time the first claim and the first eviction pass after it reactivates, before and after the
  change. If the first pass overruns its budget, decide between reading every bucket at activation
  and leaving the rest to the catch-up reminder.
- **Save time against directory size.** Measure at 1,000, 10,000 and 40,000 sessions. It should stay
  flat after the change, where today it grows.

## Related work

- [session-coordinator-sharding.md](session-coordinator-sharding.md): phase 1b, of which this is
  item 1, and the measurements above.
- [session-directory.md](session-directory.md): the directory, eviction pass, and why it wasn't
  segmented then.
- `CatalogShards` ([CatalogModels.cs](../server/src/DaprMQ.Interfaces/CatalogModels.cs)): the hash
  and the merged reads.
- [QueueActor.cs](../server/src/DaprMQ/QueueActor.cs): the segmented FIFO pattern the ReadyQueue
  follows.
