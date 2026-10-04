# DaprMQ - Behavior Instructions

**Context:** C# Dapr actor library for FIFO queuing. Stack: C# 13, .NET 10, Dapr 1.18.1+

## Output Rules

**Be extremely concise.** No explanations unless asked. Code only by default.

**For changes:**
1. Write or modify test first in /server/tests/DaprMQ.Tests/ (no exceptions)
2. Run `dotnet test` in /server/tests/DaprMQ.Tests/ immediately after code change
3. Only minimal implementation - no extra features
4. Use file:line format for references (e.g., [QueueActor.cs:96](server/src/DaprMQ/QueueActor.cs#L96))

**For questions:** One sentence. Point to code if relevant.

## TDD Workflow (Mandatory)

**New Features (Outside-In):**
1. Integration test (HTTP contract) → make compile
2. Controller test (mocked actor) → implement
3. Actor test (mocked state) → implement
4. Run Unit tests `dotnet test` in /server/tests/DaprMQ.Tests/ - all must pass
5. **Before commit:** run integration test `./build-and-test.sh`

**Bug fixes:** Create a failing test → Fix → `dotnet test` in /server/tests/DaprMQ.Tests/

**Before commit:** run integration test `./build-and-test.sh`

## Key Architecture Rules

**Use segmented storage pattern:** 100 items per segment. Batch state changes, single `SaveStateAsync()` call.

**Error handling:** Check `ErrorCode.HasValue` first. Map to HTTP status in controller ([QueueController.cs](server/src/DaprMQ.ApiServer/Controllers/QueueController.cs)).

**Actor invocation:** Use `ActorMethodNames` constants, never strings ([ActorMethodNames.cs](server/src/DaprMQ.ApiServer/Constants/ActorMethodNames.cs)).

**Validation:** Controller validates format, Actor validates business logic.

**Testing:** Mock `IQueueActorInvoker` in controllers. Mock `IActorStateManager` with `Dictionary<string, object>` in actors.

## Project Layout

- Actor: [QueueActor.cs](server/src/DaprMQ/QueueActor.cs)
- Models: [Models.cs](server/src/DaprMQ.Interfaces/Models.cs), [IQueueActor.cs](server/src/DaprMQ.Interfaces/IQueueActor.cs)
- API: [QueueController.cs](server/src/DaprMQ.ApiServer/Controllers/QueueController.cs), [DaprMQGrpcService.cs](server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)
- Tests: [QueueActorTests.cs](server/tests/DaprMQ.Tests/)
- Dashboard: [useQueueOperations.ts](dashboard/src/hooks/useQueueOperations.ts)

## Domain Knowledge (Reference Only)

**Queue ops:** Enqueue (FIFO), Dequeue, DequeueLocked (creates lock), Acknowledge (removes lock), Nack (returns item to its original position, +1 DeliveryCount, DLQ past max), ExtendLock

**Priority:** 0=fast lane, 1+=normal. Lower first.

**Locks:** In-place with `LockId`. Enables DLQ routing (`{id}-deadletter`), lock extension, FIFO preservation.
An expired lock's item returns to the position it was taken from, not the tail: every item carries a monotonic
`Sequence` stamped at enqueue, and the expiry sweep merges reclaimed items back into the head segment by it.

**Error codes:** `QueueEmpty`, `Locked`, `LockNotFound`, `LockExpired`, `ValidationError`, `ActorNotFound` ([Models.cs](server/src/DaprMQ.Interfaces/Models.cs))

**HTTP mappings:** Empty→204, Locked→423, NotFound/Expired→410, Validation→400, ActorNotFound→404

**State keys:** `metadata` (one blob per actor), `queue_{priority}_seg_{segmentNum}`, `{lockId}-lock`, `idem_{key}`, plus the lock index: `locks_exp_{bucket}` on a plain queue, `locks_session` on a session actor

**Lock expiry:** no reminder or timer. Locks are swept lazily at the top of Dequeue/DequeueLocked/SetSessionLease and on activation, using the index to find them. Plain queues expire per-item TTL and requeue to the tail; session actors hold locks for the life of the lease and bulk-restore to the *front* when it lapses. `DeliveryCount` increments per expiry, and past `MaxDeliveryCount` the item is dead-lettered.

**Sessions:** AcceptSession/RenewSessionLease/ReleaseSession, actor id `{queueId}-session-{sessionId}`. Lease via `LeaseId` header on Dequeue/Ack/Nack/ExtendLock/DeadLetter.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md) and [API_REFERENCE.md](docs/API_REFERENCE.md) for details.
