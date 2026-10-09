# Where DaprMQ's actor state reads and writes come from

As of 2026-10-04. Numbers from the perf harness's `state-reads` benchmark
(`./run-perf-test.sh --benchmark state-reads`, see [sdks/dotnet/perf/README.md](../../sdks/dotnet/perf/README.md#state-reads-benchmark)).

## How to read this

Every statement the state-reads benchmark counts traces back to a specific state call in the actors, and every step's total reconciles exactly with the code. The numbers come from four identical runs on branch `test/state-read-benchmark` (Dapr.Actors 1.18.10, Postgres state store v2).

| Step | Per | Reads | Writes | Biggest contributor |
| --- | --- | --- | --- | --- |
| enqueue-new-queue | new queue | 3 | 3 | `metadata` read and written twice: once on activation, once by Enqueue |
| enqueue | enqueue | 2 | 2 | `metadata` + tail segment |
| enqueue-batch-10 | batch of 10 | 2 | 2.1 | Same as one enqueue; one batch spills into a new segment |
| dequeue-ack | message | 5 | 6 | `metadata` read by both calls (2 of 5 reads) |
| dequeue-ack-batch-10 | message | 2.3 | 3.3 | The ack: 2 reads + 2 writes per message, unbatched |
| session-cycle | session (3 messages) | 23 | 26 | Session lock index: 1 read + 1 write per message more than a plain queue |
| consume-session | session (3 messages) | 18 | 20 | The 3 acks, as in `session-cycle`; the dequeue is one batched call |
| lock-expiry-sweep | reclaimed lock | 1.2 | 1.35 | One read + one delete per expired lock |
| topic-relay | publish (2 subscribers) | 6 | 9.1 | Relay enqueues into each subscriber queue |

Three mechanics explain almost every count:

1. **Each actor call starts with an empty state cache.** With actor reentrancy enabled (see [Program.cs](../../server/src/DaprMQ.ApiServer/Program.cs)), the Dapr SDK gives every call its own state tracker. The first read of a key in a call goes to Postgres; repeat reads in the same call are free. This is why `metadata` is read once per call, not once per actor.
2. **Writes never cause reads.** In Dapr.Actors 1.18.10, `SetStateAsync` is a blind upsert and a not-found read is cached for the rest of the call. Staged writes go out together at `SaveStateAsync`. A delete counts as a write.
3. **Reminder callbacks keep their cache.** A reminder (the topic relay tick) runs outside any call, on the actor's long-lived default tracker, so it mostly reads from memory.

In the per-key tables below, ids are collapsed: `*-lock` is every lock, `queue_*_seg_*` every segment, `session-lock_*` every session. "Read" is a `SELECT` and "write" an `INSERT`/`DELETE` against `daprmq_state`. Line numbers refer to [QueueActor.cs](../../server/src/DaprMQ/QueueActor.cs) unless another file is named, as of this commit.

## enqueue-new-queue: 3 reads, 3 writes per new queue

A brand-new queue pays for `metadata` twice. Activation reads it, finds nothing, and saves an empty one. Then Enqueue reads and writes it again, because the call's cache starts empty.

| Call | Key | Reads | Writes | Why |
| --- | --- | --- | --- | --- |
| `OnActivateAsync` | `metadata` | 1 | 1 | Reads to check it exists ([:391](../../server/src/DaprMQ/QueueActor.cs#L391)), finds nothing, and saves an empty one immediately ([:406-407](../../server/src/DaprMQ/QueueActor.cs#L406-L407)) |
| `Enqueue` | `metadata` | 1 | 1 | Fresh cache, so it reads again ([:1455](../../server/src/DaprMQ/QueueActor.cs#L1455)); writes the new count and sequence on save |
| `Enqueue` | `queue_*_seg_*` | 1 | 1 | Reads the tail segment ([:944](../../server/src/DaprMQ/QueueActor.cs#L944)), finds none, writes it with the item ([:977](../../server/src/DaprMQ/QueueActor.cs#L977)) |
| **Total** | | **3** | **3** | |

Only the activation pair is unique to a new queue. Without it, this is exactly the warm `enqueue` step.

## enqueue and enqueue-batch-10: 2 reads, 2 writes per call

An enqueue costs one read and one write each of `metadata` and the tail segment, however many items it carries. A batch of 10 costs the same as a single item.

| Call | Key | Reads | Writes | Why |
| --- | --- | --- | --- | --- |
| `Enqueue` | `metadata` | 1 | 1 | Read at the start ([:1455](../../server/src/DaprMQ/QueueActor.cs#L1455)); later items in the call hit the cache |
| `Enqueue` | `queue_*_seg_*` | 1 | 1 | Tail segment read once ([:944](../../server/src/DaprMQ/QueueActor.cs#L944)); every item appends to the cached copy, which is written once on save ([:977](../../server/src/DaprMQ/QueueActor.cs#L977), [:1131](../../server/src/DaprMQ/QueueActor.cs#L1131)) |
| **Total** | | **2** | **2** | |

**Why the batch shows 2.1 writes.** The batch queue holds 1 item before the step, so its 100th item fills segment 0. The batch that crosses that boundary starts segment 1 without reading it, since a new segment is known to be empty ([:948-953](../../server/src/DaprMQ/QueueActor.cs#L948-L953)). It then writes both segments: 10 batches, 11 segment writes. The same happens to any enqueue that straddles a 100-item boundary.

No `Enqueue` runs the lock sweep, and without an `IdempotencyKey` it never touches `idem_*` keys. With a key, each item adds one `idem_*` read and one write.

## dequeue-ack: 5 reads, 6 writes per message

`DequeueLocked` costs 3 reads and 4 writes, `Acknowledge` 2 reads and 2 writes. `metadata` is read and written by both calls, so it is 2 of the 5 reads.

| Call | Key | Reads | Writes | Why |
| --- | --- | --- | --- | --- |
| `DequeueLocked` | `metadata` | 1 | 1 | Read by the lock sweep at the top of the call ([:1472](../../server/src/DaprMQ/QueueActor.cs#L1472)); the sweep finds no due bucket and returns early |
| `DequeueLocked` | `queue_*_seg_*` | 1 | 1 | Head segment read and the item removed ([:1349](../../server/src/DaprMQ/QueueActor.cs#L1349), [:1419](../../server/src/DaprMQ/QueueActor.cs#L1419)) |
| `DequeueLocked` | `*-lock` | 0 | 1 | The new lock, written without a read ([:1572](../../server/src/DaprMQ/QueueActor.cs#L1572)) |
| `DequeueLocked` | `locks_exp_*` | 1 | 1 | The expiry bucket's id list, read and rewritten with the new lock id ([:217-220](../../server/src/DaprMQ/QueueActor.cs#L217-L220)) |
| `Acknowledge` | `metadata` | 1 | 1 | Read for the session check, written with `LockCount - 1` ([:1644](../../server/src/DaprMQ/QueueActor.cs#L1644), [:1700](../../server/src/DaprMQ/QueueActor.cs#L1700)) |
| `Acknowledge` | `*-lock` | 1 | 1 | Lock read to validate it ([:1669](../../server/src/DaprMQ/QueueActor.cs#L1669)), then deleted ([:1697](../../server/src/DaprMQ/QueueActor.cs#L1697)) |
| **Total** | | **5** | **6** | |

Acknowledge doesn't remove the id from `locks_exp_*`. That's deliberate ([:230-245](../../server/src/DaprMQ/QueueActor.cs#L230-L245)): rewriting the bucket on every ack would make a bulk dequeue O(n²), so the sweep drops ids whose lock is already gone. One side effect is that every `DequeueLocked` in the same TTL window rewrites a growing list. The count stays at one write, but the payload grows with each lock.

The step dequeues 50 items from a 150-item queue, so it never leaves segment 0. A dequeue that empties a segment adds one segment delete, which this step doesn't measure.

## dequeue-ack-batch-10: 2.3 reads, 3.3 writes per message

Locking 10 items per `DequeueLocked` cuts dequeue + ack from 5 reads and 6 writes per message to 2.3 and 3.3. The dequeue's fixed cost (3 reads, 3 writes) is shared by the batch; only the lock write stays per item. The acks are unchanged, so they are now most of the cost.

| Call | Key | Reads | Writes | Per message |
| --- | --- | --- | --- | --- |
| `DequeueLocked(count: 10)` | `metadata`, head segment, `locks_exp_*` | 3 | 3 | 0.3 / 0.3 |
| `DequeueLocked(count: 10)` | `*-lock` | 0 | 10 | 0 / 1 |
| 10 × `Acknowledge` | `metadata`, `*-lock` | 20 | 20 | 2 / 2 |
| **Per batch of 10** | | **23** | **33** | **2.3 / 3.3** |

A batch acknowledge would take the ack side from 2 reads and 2 writes per message to about 1.1 each: one `metadata` read and write per batch, plus a read and delete per lock.

## session-cycle: 23 reads, 26 writes per session

Of a session's 23 reads, 20 are on the session's own queue actor and 3 on its `SessionCoordinatorActor`. Each message costs 6 reads and 7 writes, one more of each than a plain `dequeue-ack`. The difference is the session lock index (`locks_session`): the ack must remove the lock id from it, a step plain queues skip.

Per message, on the session queue actor (`{queue}-session-{id}`):

| Call | Key | Reads | Writes | Why |
| --- | --- | --- | --- | --- |
| `DequeueLocked` | `metadata` | 1 | 1 | Read by the session sweep ([:688](../../server/src/DaprMQ/QueueActor.cs#L688)); the lease is live, so it returns early |
| `DequeueLocked` | `queue_*_seg_*` | 1 | 1 | Head segment, as in a plain dequeue |
| `DequeueLocked` | `*-lock` | 0 | 1 | The new lock |
| `DequeueLocked` | `locks_session` | 1 | 1 | Lock id appended to the session's index ([:207-210](../../server/src/DaprMQ/QueueActor.cs#L207-L210)) |
| `Acknowledge` | `metadata` | 1 | 1 | Lease check, then `LockCount - 1` |
| `Acknowledge` | `*-lock` | 1 | 1 | Read, then deleted |
| `Acknowledge` | `locks_session` | 1 | 1 | Lock id removed from the index ([:252-270](../../server/src/DaprMQ/QueueActor.cs#L252-L270)) |
| **Per message** | | **6** | **7** | |

So 3 messages cost 18 reads and 21 writes; the lease calls around them add the rest.

Per session:

| Actor | Call | Key | Reads | Writes |
| --- | --- | --- | --- | --- |
| SessionCoordinatorActor | `AcceptSession` | `metadata` | 1 | 1 |
| SessionCoordinatorActor | `AcceptSession` | `session-lock_*` | 1 | 1 |
| Session queue actor | `SetSessionLease` | `metadata` | 1 | 1 |
| Session queue actor | 3 × dequeue + ack | (table above) | 18 | 21 |
| SessionCoordinatorActor | `ReleaseSession` | `session-lock_*` | 1 | 1 |
| Session queue actor | `ClearSessionLease` | `metadata` | 1 | 1 |
| **Total** | | | **23** | **26** |

- **AcceptSession** ([SessionCoordinatorActor.cs:159](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L159)) reads the coordinator's `metadata` to find the session in its directory, and reads `session-lock_{id}` to check nobody holds it. It calls `SetSessionLease` on the session actor, then writes the new `session-lock_{id}` and the directory's `LastClaimedAt`.
- **SetSessionLease** ([:487](../../server/src/DaprMQ/QueueActor.cs#L487)) runs the session sweep, which reads `metadata`, then saves the lease into it.
- **ReleaseSession** ([SessionCoordinatorActor.cs:376](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L376)) reads and deletes `session-lock_{id}`, then calls `ClearSessionLease`, which reads and rewrites the session actor's `metadata` ([:508](../../server/src/DaprMQ/QueueActor.cs#L508)).

The session actors were activated during setup, when their items were enqueued. A cold session actor adds an activation read, plus a `RegisterSession` call to the coordinator.

## consume-session: 18 reads, 20 writes per session

The gRPC session stream (what `SessionQueueConsumer` uses) costs 5 reads and 6 writes less per session than `session-cycle`. The server dequeues all 3 messages in one call, up to the prefetch window, so 2 of `session-cycle`'s 3 dequeue calls disappear (3 reads and 3 writes each). Its polling adds an empty dequeue back, which reads `metadata` and saves nothing.

| Actor | Calls | Key | Reads | Writes |
| --- | --- | --- | --- | --- |
| SessionCoordinatorActor | accept + release | `metadata`, `session-lock_*` | 3 | 3 |
| Session queue actor | `SetSessionLease`, `ClearSessionLease` | `metadata` | 2 | 2 |
| Session queue actor | 1 × `DequeueLocked(count: 10)`, 3 items | `metadata`, head segment, `locks_session`, 3 × `*-lock` | 3 | 6 |
| Session queue actor | 3 × `Acknowledge` | `metadata`, `*-lock`, `locks_session` | 9 | 9 |
| Session queue actor | 1 × empty `DequeueLocked` | `metadata` | 1 | 0 |
| **Total** | | | **18** | **20** |

**Where the empty polls come from.** After a delivery the server's loop ([DaprMQGrpcService.cs:748-845](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L748-L845)) goes straight back to `DequeueLocked` while the prefetch window has room. The first empty poll follows every non-empty dequeue immediately, then polls repeat every 200 ms until the stream ends. The step stops consuming straight after the last ack, so there is 1. A consumer that idles out the session timeout pays one `metadata` read per 200 ms until it does.

**Stopping waits for acks.** Ack frames get no reply, so a consumer that stopped straight after acking used to cancel the call before the server had applied them, and those messages were redelivered. An early version of this step lost almost every ack that way. Stopping now half-closes the stream and waits for the server to finish first: see [session-stream-acks-lost-on-disconnect.md](../issues/resolved/session-stream-acks-lost-on-disconnect.md).

## lock-expiry-sweep: 24 reads, 27 writes for 20 reclaimed locks

The sweep reads and deletes each expired lock: 20 of the 24 reads and 20 of the 27 writes. The rest is a fixed cost of 4 reads and 7 writes, so the per-lock figure falls as more locks expire together. The step measures one `DequeueLocked` that finds 20 locks past their 1 s TTL.

| Phase | Key | Reads | Writes | Why |
| --- | --- | --- | --- | --- |
| Sweep | `metadata` | 1 | 1 | Lists the due expiry buckets; saved with `LockCount - 20` ([:544](../../server/src/DaprMQ/QueueActor.cs#L544), [:661](../../server/src/DaprMQ/QueueActor.cs#L661)) |
| Sweep | `locks_exp_*` | 1 | 1 | The due bucket's id list read ([:577](../../server/src/DaprMQ/QueueActor.cs#L577)), then deleted once empty ([:630](../../server/src/DaprMQ/QueueActor.cs#L630)) |
| Sweep | `*-lock` | 20 | 20 | Each lock read to check its expiry ([:598](../../server/src/DaprMQ/QueueActor.cs#L598)) and deleted ([:625](../../server/src/DaprMQ/QueueActor.cs#L625)) |
| Sweep | `queue_*_seg_*` | 1 | 1 | Head segment read and rewritten with the 20 items merged back in by sequence ([:644](../../server/src/DaprMQ/QueueActor.cs#L644)) |
| Dequeue | `metadata` | 0 | 1 | Cached from the sweep; written again on the dequeue's own save ([:1613](../../server/src/DaprMQ/QueueActor.cs#L1613)) |
| Dequeue | `queue_*_seg_*` | 0 | 1 | Cached; written again with one item removed |
| Dequeue | `*-lock` | 0 | 1 | The new lock for the dequeued item |
| Dequeue | `locks_exp_*` | 1 | 1 | A new bucket for the new lock's expiry: read (not found) and written |
| **Total** | | **24** | **27** | |

The sweep commits on its own (`SaveStateAsync` at [:661](../../server/src/DaprMQ/QueueActor.cs#L661)) before the dequeue commits ([:1613](../../server/src/DaprMQ/QueueActor.cs#L1613)), so `metadata` and the head segment are each written twice in one call. Folding the sweep into the operation's save would remove 2 writes per sweep; it would also make the sweep's restore and the dequeue one atomic commit.

## topic-relay: 6 reads, 9.1 writes per publish

A publish to 2 subscribers costs 2 reads and 5.1 writes on the topic, plus a full enqueue (2 reads, 2 writes) into each subscriber queue. `Publish` itself is cheap: it reads only `metadata`. The relay is where the reads happen, and how often it ticks decides the 0.1.

| Actor | Call | Key | Reads | Writes | Why |
| --- | --- | --- | --- | --- | --- |
| TopicActor | `Publish` | `metadata` | 1 | 1 | Next sequence number ([TopicActor.cs:339](../../server/src/DaprMQ/TopicActor.cs#L339), [:373](../../server/src/DaprMQ/TopicActor.cs#L373)) |
| TopicActor | `Publish` | `item_*` | 0 | 1 | The payload, written without a read ([TopicActor.cs:346](../../server/src/DaprMQ/TopicActor.cs#L346)) |
| TopicActor | `Publish` | `publish_*` | 0 | 1 | Publish record, for dedup and the reaper ([TopicActor.cs:359](../../server/src/DaprMQ/TopicActor.cs#L359)) |
| TopicActor | `Publish` | `publish-seq_*` | 0 | 1 | Sequence-to-publish-id map ([TopicActor.cs:365](../../server/src/DaprMQ/TopicActor.cs#L365)) |
| TopicActor | relay tick | `item_*` | 1 | 0 | Each newly published item read to relay it ([TopicActor.cs:598](../../server/src/DaprMQ/TopicActor.cs#L598)) |
| TopicActor | relay tick | `metadata` | 0 | 1.1 | Dispatch cursors saved each tick ([TopicActor.cs:663](../../server/src/DaprMQ/TopicActor.cs#L663)); 11 ticks in a 10-publish window |
| Subscriber queues | `Enqueue` × 2 | `metadata`, `queue_*_seg_*` | 4 | 4 | One enqueue per subscriber ([TopicActor.cs:625](../../server/src/DaprMQ/TopicActor.cs#L625)), costed as in `enqueue` |
| **Total** | | | **6** | **9.1** | |

**Why the tick reads so little.** The relay runs from a reminder, outside any call, so it uses the actor's long-lived default cache. Its `metadata`, subscriber generation and circuit-breaker reads are served from memory. That includes the circuit keys, which never exist; Dapr.Actors 1.18.10 caches "not found" ([dapr/dotnet-sdk#1909](https://github.com/dapr/dotnet-sdk/issues/1909)). The one thing it must read is each new `item_*`, because `Publish` wrote it under a call's own cache, not the default one.

**Why it's timing-dependent.** `metadata` is written once per tick with work, and the number of ticks in the window depends on how they interleave with the 1 s publishes. In 4 runs it was always 11. A slower runner could make it 10 or 12, hence this step's looser 15% tolerance.

## Patterns and opportunities

`metadata` is the most-read key in every step, because each call reads it once on an empty cache. The clearest savings, though, are two places where the code writes the same state twice.

| Opportunity | Saves | Where | Trade-off |
| --- | --- | --- | --- |
| Fold the lock sweep into the operation's save | 2 writes per sweep that reclaims anything | [:661](../../server/src/DaprMQ/QueueActor.cs#L661) then [:1613](../../server/src/DaprMQ/QueueActor.cs#L1613) | The restore and the dequeue become one atomic commit. That matches the single-`SaveStateAsync()` rule in [CLAUDE.md](../../CLAUDE.md), but the sweep's error handling would need checking |
| Don't save empty `metadata` on activation | 1 read + 1 write per new queue (3/3 → 2/2) | [:391-407](../../server/src/DaprMQ/QueueActor.cs#L391-L407) | Every caller must treat missing `metadata` as empty. Also applies to new session, dead-letter and subscriber queues |
| Drop the session index update on ack | 1 read + 1 write per session message | [:252-270](../../server/src/DaprMQ/QueueActor.cs#L252-L270) | The index would then grow with every message under a long lease, which is the reason it is pruned today |
| Keep `metadata` cached across calls | Up to 1 read per call (2 of 5 per message, 8 of 23 per session) | Dapr SDK: per-call tracker under reentrancy | Not a DaprMQ change. Needs the SDK's cross-call cache to stay correct with reentrancy, the problem behind [dapr/dotnet-sdk#1908](https://github.com/dapr/dotnet-sdk/pull/1908) |

Two costs are deliberate and should stay: plain queues never remove lock ids from `locks_exp_*` on ack, which keeps acks O(1). And `Publish` writes three records per message, which back dedup and the reaper.

The benchmark will show the effect of any of these directly. For example, folding the sweep should take `lock-expiry-sweep` from 27 to 25 writes, with every other step unchanged.
