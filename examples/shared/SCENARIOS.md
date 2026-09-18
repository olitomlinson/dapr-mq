# Example apps — scenario behavior specs

Four scenarios, each demonstrating one feature area. Every scenario is small and demonstrative (a handful of items), not a load test.

## Queue id convention

`{queuePrefix}-{suffix}`, where `queuePrefix` defaults to `examples-{language}` (e.g. `examples-dotnet`, `examples-java`, `examples-python`, `examples-typescript`) — see `API_CONTRACT.md`. This namespaces each language's pair so all 8 pods can share one DaprMQ install without colliding. A language's producer and consumer use the same `queuePrefix` by default (both read it from the same `DAPRMQ_QUEUE_PREFIX` env var / the same `PUT /config` override), so they interoperate out of the box.

**Important SDK constraint**: none of the four SDKs expose a plain "dequeue without creating a lock" method — every SDK's dequeue operation is the locking variant (`DequeueLockedAsync` / `dequeueLocked` / `dequeue_locked` / `dequeueLocked`), which always creates a lock that must then be acknowledged, dead-lettered, or left to expire. There is no separate fire-and-forget dequeue. All scenarios below are written around this.

---

## `basic` — enqueue/dequeue

Suffix: `basic` (e.g. `examples-dotnet-basic`).

**Producer** (`POST /scenarios/basic/run`): enqueues 3 plain items in a single `Enqueue` call:
```json
{"n": 1, "message": "hello from producer"}
{"n": 2, "message": "hello from producer"}
{"n": 3, "message": "hello from producer"}
```
Default priority (1), no session, no idempotency key. Logs each item as part of the enqueue.

**Consumer** (`POST /scenarios/basic/run`): calls dequeue with ack (`count=5`, default ttl), and for every item returned, immediately acknowledges it. Logs each item retrieved and acknowledged, or the empty-queue note if none were available.

---

## `ack-deadletter` — acknowledgements + deadletters

Suffix: `ackdlq` (e.g. `examples-dotnet-ackdlq`). DLQ is the DaprMQ-managed `{queueId}-deadletter`, i.e. `examples-dotnet-ackdlq-deadletter` — an ordinary, independently dequeuable queue.

**Producer**: enqueues 3 items, each carrying an `outcome` field:
```json
{"outcome": "ack", "n": 1}
{"outcome": "deadletter", "n": 2}
{"outcome": "expire", "n": 3}
```

**Consumer**: dequeue with ack, `count=3`, `ttlSeconds=10`. For each dequeued item, branch on its `outcome` field:
- `"ack"` → `Acknowledge` it.
- `"deadletter"` → `DeadLetter` it; log the resulting `dlqId` from the response.
- `"expire"` → do nothing (leave the lock to expire).
- (any item with no `outcome` field, e.g. from manual curling, defaults to `"ack"`.)

Then sleep ~11 seconds (`ttlSeconds` + 1), and dequeue from the *same* queue again — this should return the `expire` item again (redelivered because its lock expired), which the consumer then acknowledges (drain it) and logs.

Finally, dequeue once from `{queueId}-deadletter` to show the dead-lettered item sitting there; log its contents, then acknowledge it there too (fully drain the demo's DLQ so re-running the scenario later isn't confused by leftovers).

---

## `priority` — fast lane vs normal

Suffix: `priority` (e.g. `examples-dotnet-priority`).

**Producer**: enqueues 3 items at priority `1` (normal) first:
```json
{"priority": 1, "n": 1}
{"priority": 1, "n": 2}
{"priority": 1, "n": 3}
```
then 3 items at priority `0` (fast lane):
```json
{"priority": 0, "n": 4}
{"priority": 0, "n": 5}
{"priority": 0, "n": 6}
```
— deliberately priority-inverted enqueue order (normal items enqueued *before* the fast-lane ones). Logs each enqueue including the priority used.

**Consumer**: dequeues with ack, `count=10`, in one call, acknowledging each item as it's returned. Logs the retrieval order — this should show the 3 fast-lane items (`n=4,5,6`) surfacing before the 3 normal items (`n=1,2,3`), despite being enqueued later, demonstrating priority ordering.

---

## `sessions` — session-scoped ordering + exclusivity

Suffix: `sessions` (e.g. `examples-dotnet-sessions`).

**Producer**: enqueues 4 items, 2 per session, each carrying a per-session sequence number:
```json
{"sessionId": "session-a", "seq": 1}
{"sessionId": "session-a", "seq": 2}
{"sessionId": "session-b", "seq": 1}
{"sessionId": "session-b", "seq": 2}
```
Logs each enqueue including its session id.

**Consumer**: drains both sessions in two rounds, deliberately using a different `AcceptSession` mode each time so both are demonstrated:

*Round 1 — "any available" mode:* `AcceptSession(queueId, sessionId=null, leaseSeconds=30)` — the server picks whichever of `session-a`/`session-b` is currently unclaimed. Log the returned `sessionId` and `leaseId` (which one you got is implementation-chosen, not fixed).

*Round 2 — targeted mode:* accept the *other* known session by name — `AcceptSession(queueId, sessionId=<the one round 1 didn't return>, leaseSeconds=30)`. Log the returned `leaseId`.

For each round, once a lease is held:
1. Dequeue with ack against the derived session queue id `{queueId}-session-{sessionId}` (construct this string yourself — the SDKs don't build it for you in the manual API), passing the `leaseId`. Expect both of that session's items back in order; log them.
2. `Acknowledge` each item (with the `leaseId`).
3. `ReleaseSession(queueId, sessionId, leaseId)`.

If a round's `AcceptSession` call comes back empty (no session currently available — e.g. the producer hasn't run yet), log that per the usual empty-queue tolerance convention and skip that round's dequeue/ack/release steps, but still attempt the other round.

Log the full accept → dequeue → ack → release sequence for both sessions, in order, so the stdout output demonstrates per-session FIFO plus the accept/release lifecycle. (This uses the manual/unary session API, not the managed `SessionQueueConsumer` — the manual API makes each step's logging explicit, which is the point of this demo.)

---

## `idempotency` — message deduplication

Suffix: `idempotency` (e.g. `examples-dotnet-idempotency`).

**Producer** (`POST /scenarios/idempotency/run`): generates one idempotency key suffixed with the current time (so repeated runs of this scenario don't collide with a previous run's key still inside the dedup TTL window), then makes three separate `Enqueue` calls:
1. One item (`n=1`) with that key. Expected: `itemsEnqueued=1`, `itemsDeduplicated=0`.
2. A second item (`n=2`) reusing the *same* key. Expected: `itemsEnqueued=0`, `itemsDeduplicated=1` — silently dropped, never enters the queue.
3. A third item (`n=3`) with a different key. Expected: `itemsEnqueued=1`, `itemsDeduplicated=0`.

Logs each call's `itemsEnqueued`/`itemsDeduplicated` counts so the dedup outcome is visible in stdout without needing to inspect the queue directly.

**Consumer** (`POST /scenarios/idempotency/run`): dequeues with ack (`count=5`) and logs the returned item count and `n` values, explicitly noting the expectation of exactly 2 items (`n=1`, `n=3`) since `n=2` should never have been enqueued. Acknowledges whatever is returned.

---

## Per-language method cheat sheet

Exact calls to use (see each SDK's `docs/CLIENT_SDK.md` for full context — these are pulled from there):

**`.NET`** (`DaprMQ.Client`): `EnqueueAsync(queueId, items)` with `new EnqueueItemDto(payload, priority: N, sessionId: sid)`; `DequeueLockedAsync(queueId, count, ttlSeconds, leaseId)` → nullable `DequeueLockedResult` with `.Items[].Item/.LockId`; `AcknowledgeAsync(queueId, lockId, leaseId)`; `DeadLetterAsync(queueId, lockId, leaseId)`; `AcceptSessionAsync(queueId, sessionId, leaseSeconds)` → nullable `SessionLease{SessionId,LeaseId}`; `ReleaseSessionAsync(queueId, sessionId, leaseId)`.

**Java** (`com.daprmq.client`): `enqueue(queueId, List<EnqueueItem>)` with `new EnqueueItem(item, priority, idempotencyKey, sessionId)`; `dequeueLocked(queueId, count, ttlSeconds, leaseId)` → nullable result with `.items()[].item()/.lockId()`; `acknowledge(queueId, lockId, leaseId)`; `deadLetter(queueId, lockId, leaseId)`; `acceptSession(queueId, sessionId, leaseSeconds)` → nullable lease with `.sessionId()/.leaseId()`; `releaseSession(queueId, sessionId, leaseId)`.

**Python** (`daprmq_client`, async): `await client.enqueue(queueId, [EnqueueItem(item=..., priority=N, session_id=sid)])`; `await client.dequeue_locked(queueId, count=N, ttl_seconds=N, lease_id=lid)` → `None` or result with `.items[].item/.lock_id`; `await client.acknowledge(queueId, lock_id, lease_id=lid)`; `await client.dead_letter(queueId, lock_id, lease_id=lid)`; `await client.accept_session(queueId, session_id=sid, lease_seconds=30)` → `None` or lease with `.session_id/.lease_id`; `await client.release_session(queueId, session_id, lease_id)`.

**TypeScript** (`daprmq-client`): `client.enqueue(queueId, [{ item, priority, sessionId }])`; `client.dequeueLocked(queueId, { count, ttlSeconds, leaseId })` → `null` or result with `.items[].item/.lockId`; `client.acknowledge(queueId, lockId, { leaseId })`; `client.deadLetter(queueId, lockId, { leaseId })`; `client.acceptSession(queueId, { sessionId, leaseSeconds })` → `null` or lease with `.sessionId/.leaseId`; `client.releaseSession(queueId, sessionId, leaseId)`.

Derived session queue id for the manual API, all languages: `` `${queueId}-session-${sessionId}` `` (string concatenation — the SDKs do not build this for you outside the managed `SessionQueueConsumer`).
