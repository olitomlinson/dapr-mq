# The lock-expiry sweep reads an unbounded number of lock rows per turn

**Status:** Open. Measured on 2026-10-17 and 2026-10-18 with a 100,000-message KEDA load test on a local kind cluster (`examples/keda/test/load.sh`, 81 workers × 1 handler, window 100, 1ms per item). It hasn't been fixed. The workaround is fewer workers with more handlers each (`autoscale.workerMaxHandlers`).

**Severity:**
- **High** for a plain queue with a large, bursty backlog consumed by many streams. Queue actor turns take seconds to minutes, callers time out, locks expire and are redelivered, and throughput collapses.
- Before the turn guard ([#77](https://github.com/olitomlinson/dapr-mq/pull/77)), the slow turns also triggered [dapr/dapr#10835](https://github.com/dapr/dapr/issues/10835): the sidecar dispatched calls into the queue actor while a turn was still in progress, which corrupted its state. See [actor-turns-overlap-when-callers-cancel.md](resolved/actor-turns-overlap-when-callers-cancel.md).
- **Low** for steady traffic with few consumers.

**Affects:** `SweepExpiredLocksAsync` on plain queues ([QueueActor.cs:650](../../server/src/DaprMQ/QueueActor.cs#L650)). It runs at the top of every `Dequeue` and `DequeueLocked` ([QueueActor.cs:1350](../../server/src/DaprMQ/QueueActor.cs#L1350), [QueueActor.cs:1625](../../server/src/DaprMQ/QueueActor.cs#L1625)), so it runs on every Consume-stream refill.

---

## Summary

The sweep's budget (`DAPRMQ_LOCK_SWEEP_BATCH_SIZE`, default 200) limits how many **expired locks it restores**. It doesn't limit how many **index entries it reads**. Every entry in a due bucket costs one state-store read, whether its lock has expired, is still live, or has already been acknowledged. With actor reentrancy on, none of those reads is cached from one call to the next.

In the load test, sweeps took a **median of 13s and up to 118s**, reading up to **6,565 index entries in one sweep**. Most of those were for locks that had already been acknowledged.

## Measurements

These come from a diagnostic build that logged every sweep taking at least 500ms, and every queue actor call taking at least 1s. The build is described under "How to confirm" below. From one run, 603 slow sweeps:

| Per slow sweep | Minimum | Median | Maximum |
|---|---|---|---|
| Time taken | 0.5s | **13.0s** | **118.0s** |
| Index entries held by the due buckets | 0 | 557 | **6,565** |
| …whose lock row was already gone (acknowledged or dead-lettered) | 0 | 100 | 5,354 |
| …whose lock hadn't expired yet | 0 | 0 | 990 |
| Expired locks restored | 0 | 200 | 200 (the budget) |
| Due buckets | 1 | 3 | 10 |

Slow calls in the same run: 89 `DequeueLocked` (median 1.8s, maximum 19.8s), 29 `ExtendLockBatch` and 8 `AcknowledgeBatch`. The gateway's settle calls time out after 10s ([DaprMQGrpcService.cs:39](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L39)). In a later run with the settle-failure reasons from #75, every failed settle read `AcknowledgeBatch of N lock(s) timed out after 10s waiting for the queue actor`.

## Problem 1: the budget counts restores, not reads

In the loop over a due bucket's ids ([QueueActor.cs:714-717](../../server/src/DaprMQ/QueueActor.cs#L714-L717)):

- An id whose lock row is gone is dropped with `continue`, and doesn't count against the budget. Acknowledge deliberately leaves the bucket index alone (an over-approximation, so settling stays O(1)). So after a burst, a bucket can be almost entirely ids that are already acknowledged.
- An id whose lock hasn't expired is kept as a survivor, and doesn't count against the budget either. A bucket is due from its earliest possible expiry.
- Only a restored lock decrements `budget`.

So one sweep reads every id in every due bucket until it has restored 200 locks. That can be thousands of reads.

## Problem 2: with reentrancy on, nothing is cached between calls

When a call arrives with a reentrancy context, which in DaprMQ is every call, `Dapr.Actors` 1.18.10 gives it a fresh, empty state tracker. It does this through `SetStateContext` in `ActorManager.DispatchInternalAsync` and `GetContextualStateTracker` in `ActorStateManager`. Every read in a call therefore goes to the state store through the sidecar. The lock rows a sweep read on the previous `DequeueLocked` are read again on the next one, along with the bucket lists, the metadata and the head segment.

## Problem 3: bursts put thousands of locks in a few buckets

Locks are indexed by expiry, in 5-second buckets ([QueueActor.cs:172](../../server/src/DaprMQ/QueueActor.cs#L172)), and expiry is dequeue time plus the lock TTL (30s for Consume streams). When 81 streams each dequeue 100 at startup within a few seconds, about 8,000 locks share a handful of buckets. Each wave of redeliveries clusters again.

## Problem 4: a changed bucket is rewritten whole

When a bucket's survivors differ from its ids, the whole list is written back ([QueueActor.cs:753](../../server/src/DaprMQ/QueueActor.cs#L753)). That's thousands of ids per sweep, saved in the same transaction as everything else the turn wrote.

## How it compounds

Long sweeps make `DequeueLocked` slow, and calls queue behind them on the one actor. `AcknowledgeBatch` calls time out after 10s, and their locks expire. The sweep then restores those items to the front of the queue, at negative segment numbers, and they're redelivered and acknowledged again. That puts more ids into the buckets and makes the next sweep longer. With 81 workers × 1 handler, the 100k test never drained. With 10 workers × 10 handlers, it drained in about 4 minutes, at roughly 300–440 messages/s.

## Suggested directions

1. **Bound the reads, not just the restores.** Stop after examining a fixed number of index entries per turn, counting gone, live and expired alike. Resume where the last sweep stopped, for example with a per-bucket cursor in metadata. The backlog then catches up over later operations, with every turn short.
2. **Prune acknowledged ids cheaply.** Avoid a full rewrite of a large bucket on every sweep. For example, rewrite only once a bucket's stale share passes a threshold, or split buckets into fixed-size chunks so a rewrite is bounded.
3. **Avoid needing the sweep on the hot path.** Run it only when a due bucket exists and enough time has passed since the last sweep, rather than on every `DequeueLocked`.
4. **Reduce bucket clustering.** Spread lock expiries, for example with jitter on the TTL, so a burst doesn't land in one bucket.

The first is the essential fix. The others reduce how much work there is.

## How to confirm

1. On kind, run `examples/keda/test/load.sh --publishers 10 --messages 10000 --publish-mode batch --ramp-interval 0` with the examples worker at `autoscale.maxReplicaCount=100`, `workerMaxHandlers=1`, `workerBatchSize=100` and `workerItemDelayMs=1`.
2. To see the sweeps, add temporary logging to `SweepExpiredLocksAsync`:
   - a `Stopwatch` around the sweep
   - counters for ids examined whose row is gone, ids still live, and locks restored
   - a warning when the sweep takes at least 500ms

   Also time each call, from `OnPreActorMethodAsync` to `OnPostActorMethodAsync`.
3. Set the actor hosts to log at Warning, and stream their logs to a file with `kubectl logs -f` for the whole run. At default verbosity the kubelet rotates the logs away within minutes.
4. Don't infer overlapping sweeps from log timestamps. The console logger writes from a background queue, so under CPU starvation lines are stamped late. Count overlaps inside the turn instead (see [actor-turns-overlap-when-callers-cancel.md](resolved/actor-turns-overlap-when-callers-cancel.md)).
