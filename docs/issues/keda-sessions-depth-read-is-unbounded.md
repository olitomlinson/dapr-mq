# KEDA sessions-mode depth read is unbounded, and so is the session directory it walks

**Status:** Open. Found while assessing the admin console's load on the broker. Not yet reproduced
under load: the numbers below come from the code paths, not from measurements.

**Severity:**
- **High** for any sessions-mode ScaledObject on a queue with high session churn. The read load is
  continuous, grows with every session ever created, and does not depend on traffic.
- **Low** for queues with a small, stable set of sessions.

**Affects:** `mode: sessions` KEDA triggers through `DaprMQ.Operator`. Two of the causes, the
directory size (Problem 3) and its storage as one blob (Problem 4), also affect `AcceptSession` on
the broker's hot path, whether or not KEDA is in use.

---

## Summary

Each sessions-mode depth read loads the queue's whole session directory, then reads every
session's metadata. That happens about once a second for each ScaledObject, regardless of
traffic. The directory only shrinks through an hourly sweep that evicts about 100 sessions an
hour. A workload that creates sessions faster than that grows the directory, and therefore the
cost of every read, without limit.

## Problem 1: a full walk of every session about once a second, for each ScaledObject

How a sessions-mode read works:

1. KEDA's `external-push` trigger holds open a `StreamIsActive` stream. The operator loops on it
   every `streamPollIntervalMs`, which defaults to 1000 ms
   ([DaprMQExternalScaler.cs:40](../../server/src/DaprMQ.Operator/Scaling/DaprMQExternalScaler.cs#L40)).
   `GetMetrics` (every `pollingInterval`) and `IsActive` add to that.
2. `CachingWorkerDepthClient` caches each query for `depthCacheTtlMs`, which also defaults to
   1000 ms. Because the poll interval and the TTL are equal, nearly every stream tick misses the
   cache.
3. Each miss runs `QueueDepthService.GetSessionsDepthAsync` on a worker
   ([QueueDepthService.cs:59](../../server/src/DaprMQ/QueueDepthService.cs#L59)):
   - one read of the coordinator's `metadata`, which holds the **entire** directory
     ([QueueActorStateReader.cs:104](../../server/src/DaprMQ/QueueActorStateReader.cs#L104));
   - then **one state read per session** in the directory, 16 at a time
     ([QueueDepthService.cs:64](../../server/src/DaprMQ/QueueDepthService.cs#L64)).

Each read goes from the worker app to its Dapr sidecar to Postgres, as a primary-key lookup.

**Cost:** about **N + 1 Postgres reads per second** for each distinct sessions-mode query, where N
is the directory size. 10k sessions is about 10k reads/s, around the clock. Empty sessions that are
still waiting to be evicted count towards N, so an idle queue costs as much as a busy one. All of
these reads share the worker sidecar's state-store connections with actor `SaveStateAsync` writes.

ScaledObjects with the same queue and mode share a cache entry. Different queues add their costs
together.

## Problem 2: slow walks overlap and pile up

`CachingWorkerDepthClient` starts a new read once the entry's TTL has passed, even if the previous
read is still running
([WorkerDepthClient.cs:65](../../server/src/DaprMQ.Operator/Scaling/WorkerDepthClient.cs#L65)).
Once a walk takes longer than the TTL (about 16k sessions at roughly 1 ms per read), a second walk
starts beside the first, then a third. Each one runs 16 reads in parallel.

This is positive feedback: more concurrent walks make Postgres slower, which makes each walk
longer, which lets more walks overlap. In the end walks exceed KEDA's gRPC deadline and the scaler
reports `Unavailable` (Problem 5). By then Postgres is still serving the abandoned walks for every
other operation.

## Problem 3: the session directory can grow without limit

A session's entry is removed only by `SessionCoordinatorActor`'s directory sweep:

- The sweep is a reminder that fires **once an hour**
  ([SessionCoordinatorActor.cs:90](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L90)).
- Each run checks at most **200** candidates
  ([SessionCoordinatorActor.cs:91](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L91)).
- An entry is evicted only after **two** consecutive empty readings. The second check is scheduled
  300 s after the first, but it can only happen on the next run, an hour later.

So each eviction costs two of the 200 slots per hour. That caps the steady-state eviction rate at
about **100 sessions an hour, or 2,400 a day**, for each queue. A non-empty session also backs off
for between 1 hour and 1 day before it is checked again.

Any workload that uses a fresh session id per unit of work (per order, cart or conversation) and
creates more than about 100 sessions an hour grows the directory forever. That growth feeds
Problems 1, 2 and 4 directly.

## Problem 4: the directory is a single blob that is rewritten on every registration and claim

`SessionCoordinatorMetadata.SessionDirectory` is one `Dictionary<string, SweepCandidate>` stored in
one `metadata` key. The whole dictionary is copied and rewritten:

- on every new session's `RegisterSession`
  ([SessionCoordinatorActor.cs:143](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L143));
- on every `AcceptSession`, to update `LastClaimedAt`
  ([SessionCoordinatorActor.cs:251](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L251));
- on every sweep run.

An any-available `AcceptSession` also sorts the full directory by `LastClaimedAt` and checks the
lease of each candidate in turn until it finds a free one
([SessionCoordinatorActor.cs:197](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L197)).

So the cost of each write and each claim is O(N) in the directory size. That breaks the project's
rule of segmented storage with 100 items per segment. It also turns every scaler read into an
O(N)-byte blob fetch before the per-session reads even begin.

## Problem 5: one failed read fails the whole metric

`Parallel.ForEachAsync` fails the whole walk if any single session read throws. The scaler maps that
to `Unavailable` (by design, it never reports 0 on failure), and KEDA's `fallback` takes over. The
chance that some read fails grows with N, so large session queues lose autoscaling more often, and
that happens exactly when they are under the most load.

---

## Suggested directions

In rough order of value against effort:

1. **Keep a running count of non-empty sessions on the coordinator (O(1) read).** Session actors
   already call the coordinator in `RegisterSession`. Add an `empty → non-empty` and
   `non-empty → empty` signal, sent from the session actor when its `ItemCount + LockCount` crosses
   zero, and keep `NonEmptySessions` (plus total ready and locked counts, if wanted) in a small
   separate key. The scaler then reads one key instead of N + 1. This removes Problems 1, 2 and 5
   for the scaler. Getting the count correct across failures needs care: an idempotent
   transition keyed on session id, and the sweep reconciling any drift.
2. **Never start a second read while one is in flight.** In `CachingWorkerDepthClient`, keep
   returning the running task after the TTL expires, and only start a new read once it finishes.
   This is a small change that removes Problem 2 on its own.
3. **Separate the poll interval from the cache TTL in sessions mode.** For example, default
   sessions-mode stream polling to 5–15 s, or make the TTL a multiple of the walk time. This is
   a cheap stopgap until direction 1 lands.
4. **Make the sweep keep up with session creation.** Size the batch from the directory size or
   the number of entries due, or re-run immediately while due candidates remain. Run the second
   empty check on its own short timer rather than waiting for the next hourly run.
5. **Split the directory into segments.** Store entries in 100-entry segments, following the
   queue storage pattern, plus a small index. Keep a separate structure (or bucketed key) of
   claimable sessions so an any-available claim doesn't sort everything. This fixes Problem 4 for
   both the hot path and the scaler.
6. **Tolerate individual read failures.** Until direction 1 lands, treat a failed session read as
   unknown and report the metric from the reads that succeeded, failing only past a threshold
   (for example more than 5% failed). The scaler should still never report 0 on a wholesale
   failure.

## How to confirm

- **Unit tests:** with a mocked `IQueueActorStateReader` (as in `QueueDepthServiceTests`), count
  reads per `GetDepthAsync` for N sessions; it should be N + 1. With a fake `TimeProvider` and a
  slow inner client, show `CachingWorkerDepthClient` running two reads at once after the TTL
  expires.
- **Load test:** register 10k sessions on one queue and create a sessions-mode `external-push`
  ScaledObject (as in [examples/keda/consumer-scaledobject.yaml](../../examples/keda/consumer-scaledobject.yaml)).
  Measure Postgres query rate and worker sidecar latency with no message traffic, then measure
  `AcceptSession` p99 at 1k and 10k sessions.
- **Directory growth:** create sessions at 500 an hour for 24 hours, draining each one, and
  confirm the directory size climbs steadily rather than levelling off.
