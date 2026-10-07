# Batch acknowledge

**Status:** proposed, not started. Answers open question 9 of
[readiness-and-retries.md](readiness-and-retries.md) (fan-in on one queue).

## Context

An acknowledge settles exactly one lock, end to end. The REST body is `{ "lockId": … }`, the gRPC
`AcknowledgeRequest` has one `lock_id`, and `QueueActor.Acknowledge`
([QueueActor.cs](../server/src/DaprMQ/QueueActor.cs), `Acknowledge(AcknowledgeRequest)`) takes one.
A consumer that dequeues 1,000 locked items in one call (bulk dequeue allows up to 1,000) must make
1,000 separate acknowledge calls to settle them.

Each of those calls costs, in sequence on the queue's single actor:

- one HTTP request, and on a split deployment two sidecar hops (gateway sidecar to worker sidecar);
- one actor turn, during which every other call on that queue waits;
- `GetMetadataAsync`, `TryGetStateAsync("{lockId}-lock")`, `RemoveStateAsync`,
  `DeindexSessionLockAsync` (on a session queue, a rewrite of the whole `locks_session` list),
  `SetMetadataAsync` (`LockCount - 1`), and one `SaveStateAsync` transaction;
- `ScheduleBlobReapingIfNeeded` for an offloaded payload (an actor call to the blob reaper).

This is what surfaced in the bulk test (`BulkOperations_10000Messages_SingleEnqueue_ParallelDequeue`).
10,000 concurrent acks queued behind one actor, and the slowest waited long enough to be cut off
(before the retry window and the per-call limit were separated). The server now handles slow calls
correctly, but the cost stays: one actor turn and one state transaction per lock. It also goes against
the project rule to batch state changes into a single `SaveStateAsync()`. Bulk dequeue already follows
that rule, creating up to 1,000 locks in one call.

## Proposal

Add an acknowledge that settles up to 1,000 locks in one call: one request, one actor turn, one state
transaction.

### Semantics

- **Results per lock, never all-or-nothing.** One lock that has already expired, or was already
  acknowledged, must not fail the rest. The response lists each lock id with its outcome, in request
  order:

  | Outcome | Meaning |
  |---|---|
  | `ACKNOWLEDGED` | Settled by this call |
  | `LOCK_NOT_FOUND` | No such lock: never existed, or already settled (including by an earlier attempt of this same batch) |
  | `LOCK_EXPIRED` | Plain queue only: its TTL passed, so the item is already returning to the queue (same rule as single acknowledge) |
  | `INVALID_LOCK_ID` | Empty or malformed id |

- **Whole-call errors** stay whole-call: a missing or wrong `LeaseId` on a session queue
  (`SESSION_LEASE_EXPIRED` / `INVALID_LEASE_ID`, checked once for the batch), an empty list, more than
  1,000 ids, or duplicate ids (`VALIDATION_ERROR`). A delivery failure is reported as `UNAVAILABLE` or
  `DELIVERY_UNKNOWN`, as for every operation.
- **The call succeeds when the request was valid,** whatever the per-lock outcomes: REST `200`, gRPC
  OK. Callers read the per-lock results.
- **Safe to retry.** Re-sending a batch after `DELIVERY_UNKNOWN` is harmless. Locks the first attempt
  settled come back `LOCK_NOT_FOUND`, which in a retry means "already done". So the SDKs **may retry an
  unknown batch acknowledge automatically**, which single acknowledge doesn't do today (open question 4
  of the readiness proposal). The SDK should report locks that turned `LOCK_NOT_FOUND` on a retry
  distinctly from those that were `LOCK_NOT_FOUND` on the first attempt, or document that after an
  automatic retry `LOCK_NOT_FOUND` means "settled".
- **Ordering:** results follow request order. Settlement is one transaction, so either every
  `ACKNOWLEDGED` lock in the batch is settled or none is (if the save fails, the whole call fails and
  is safe to retry).

### Actor

`QueueActor.AcknowledgeBatch(AcknowledgeBatchRequest { LockIds, LeaseId })`:

1. Validate the request (non-empty, ≤ 1,000, no duplicates); authorise the session lease once.
2. `GetMetadataAsync` once.
3. For each id: `TryGetStateAsync("{id}-lock")`, classify (not found, expired on a plain queue), and
   `RemoveStateAsync` the ones that settle. Collect their item JSON for blob reaping.
4. On a session queue, remove all settled ids from `locks_session` in one rewrite (not one per lock).
5. `SetMetadataAsync(LockCount - settledCount)` once, then one `SaveStateAsync`.
6. Schedule blob reaping for all offloaded payloads in one call to the reaper (extend it to take a
   list, or loop after the save; reaping is best-effort and never affects the result).

Single `Acknowledge` can then delegate to the batch path with one id, so there is one settlement
implementation. Its response shape and status codes stay as they are.

### API

| Surface | Shape |
|---|---|
| REST | `POST /queue/{queueId}/acknowledge-batch`, body `{ "lockIds": ["…", …] }`, optional `lease-id` header. Response `{ "success": true, "itemsAcknowledged": n, "results": [{ "lockId", "outcome" }] }` |
| gRPC | `rpc AcknowledgeBatch(AcknowledgeBatchRequest) returns (AcknowledgeBatchResponse)`; `repeated string lock_ids`, `string lease_id`; response carries `repeated AcknowledgeResult results` |
| Actor method name | `ActorMethodNames.AcknowledgeBatch` |
| .NET SDK | `Task<AcknowledgeBatchResult> AcknowledgeBatchAsync(string queueId, IReadOnlyList<string> lockIds, string? leaseId = null, CancellationToken ct = default)` |
| Python | `async def acknowledge_batch(queue_id, lock_ids, *, lease_id=None) -> AcknowledgeBatchResult` |
| TypeScript | `acknowledgeBatch(queueId, lockIds, { leaseId?, signal? }): Promise<AcknowledgeBatchResult>` |
| Java | `AcknowledgeBatchResult acknowledgeBatch(String queueId, List<String> lockIds, String leaseId)` |

A separate endpoint, rather than a `lockIds` field on the existing one, keeps single acknowledge's
response and error codes exactly as they are for existing callers.

### Dead-letter

`DeadLetter` has the same one-lock-per-call shape. A `deadletter-batch` with the same per-lock
semantics is a natural follow-on, but needs care: each dead-lettered item is enqueued onto
`{queueId}-deadletter`, an actor-to-actor call. Batching that means one enqueue of N items to the DLQ
actor per batch. Out of scope for the first version (open question 2).

### Not changed

- **`ConsumeSession` stream acks.** They already travel as frames on an open stream, with no per-ack
  HTTP request. They could later be applied in groups server side, but that is a separate change.
- **Lock expiry, delivery counts and the lock index buckets** (`locks_exp_{bucket}`): untouched. The
  sweep already skips ids whose lock is gone.

## Limits and costs

- **1,000 locks per call,** matching bulk dequeue's `count` limit. One call settles exactly what one
  bulk dequeue produced.
- **A batch holds the actor's turn longer** than one ack: other callers on that queue wait for the
  whole batch. The cap keeps that bounded, and total time for N locks drops sharply.
- **State store transactions:** one save of up to 1,000 removals plus one metadata write. That is the
  same scale as bulk dequeue's save of up to 1,000 lock creations, so any store that supports bulk
  dequeue supports this.

## Tests (TDD order, per CLAUDE.md)

1. **Integration (HTTP contract)**, on both the combined and the split stack:
   - acknowledge 1,000 locks from one bulk dequeue in one call; all `ACKNOWLEDGED`, queue `LockCount`
     back to 0, nothing redelivered after the TTL;
   - mixed batch: valid, already-acknowledged, expired (plain queue) and empty ids get their own outcomes,
     and the valid ones settle;
   - the same batch sent twice: the second reports `LOCK_NOT_FOUND` for all;
   - session queue: correct lease settles; wrong lease rejects the whole call; `locks_session` no longer
     holds the settled ids;
   - more than 1,000 ids, an empty list, or duplicates give `400`;
   - rewrite `BulkOperations_10000Messages_SingleEnqueue_ParallelDequeue`'s ack phase as 10 batch calls
     (keep a variant that still sends single acks in parallel, as the server's fan-in stress test).
2. **Controller / gRPC service** (mocked `IQueueActorInvoker`): request validation, mapping of per-lock
   results, whole-call errors, delivery failures (`UNAVAILABLE`, `DELIVERY_UNKNOWN`).
3. **Actor** (mocked `IActorStateManager` with a `Dictionary<string, object>`): one `SaveStateAsync`
   per batch, `LockCount` decremented by the settled count, session index rewritten once, blob reaping
   for offloaded payloads only, single `Acknowledge` delegating with unchanged results.
4. **SDKs:** unit tests per SDK (result mapping, automatic retry of an unknown batch), and new rows in
   the SDK integration matrix ([INTEGRATION_TESTS.md](../sdks/testing/INTEGRATION_TESTS.md)) for all
   four SDKs.

## Phases

1. Actor `AcknowledgeBatch` + single `Acknowledge` delegating to it.
2. REST and gRPC endpoints.
3. .NET SDK, including the automatic retry of an unknown batch, then Python, TypeScript and Java.
4. Docs: API reference, each SDK guide, and the "Patterns" section of the SDK timeouts guide (prefer
   batch acknowledge over many concurrent single acks).

## Open questions

1. **Retry reporting:** after an automatic retry, should the SDK rewrite `LOCK_NOT_FOUND` to
   "settled", or keep the server's outcome and document what it means?
2. **Dead-letter batch:** in this proposal, or a follow-on once batch acknowledge has shipped?
3. **Batch ExtendLock:** worth adding for long-running batch consumers, with the same per-lock
   semantics?
4. **Partial-failure status code:** keep `200` when every lock failed (for example, all expired), or
   return a distinct status so naive callers notice?
