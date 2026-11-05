# One session per enqueue

**Status:** implemented (server, REST, gRPC, SDK doc comments, docs, the Python example producer).
Still to do: the `pubsub.daprmq` component check, which lives in its own repository. A
prerequisite for [scheduled-delivery.md](scheduled-delivery.md).

## Context

Today one enqueue call can mix items for different sessions, and items with no session. Both enqueue
endpoints group the items by target actor and make one actor `Enqueue` call per group:

- REST: `QueueController.Enqueue`
  ([QueueController.cs](../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs)), which groups
  by `ResolveTargetActorId(queueId, item.SessionId)`.
- gRPC: `DaprMQGrpcService.Enqueue`
  ([DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)), which does
  the same and is commented "Mirrors QueueController.Enqueue".

**A mixed batch is not atomic, and a failure doesn't say what happened.**
- Each group commits on its own actor, so if a later group fails, the endpoint returns an error after
  earlier groups have already committed.
- The caller can't tell which items were enqueued.
- Retrying the whole batch duplicates the committed groups at any target with dedup off. Without
  idempotency keys, dedup can't help at all.

That falls short of the rule that a success response means the items were processed and a failure
means they weren't.

Azure Service Bus doesn't allow it either. The session id is set on each message, but all messages in
one send call must share it: "All messages passed to the same sendMessages() call should have the same
sessionId (if using sessions)"
([ServiceBusSender](https://learn.microsoft.com/en-us/javascript/api/@azure/service-bus/servicebussender)).

Scheduled delivery depends on this too: its `ScheduleActor` needs a clear outcome for each target it
delivers to, and one call per session gives it that.

## Proposal

**Reject a batch whose items don't all have the same session id,** and remove the per-target grouping.
`sessionId` stays on each item, as in Azure Service Bus, so the API shape and the SDKs don't change.

### Rule

- All items in one enqueue must have the same `sessionId`: either all without one, or all with the
  same value.
- A mix of items with and without a session is rejected too, because it also spans two actors.
- The check runs in the endpoint, before any actor call, alongside the existing format checks
  (controller validates format, per CLAUDE.md).
- Rejection:
  - REST: `400` with `ApiErrorResponse("All items in one enqueue must have the same sessionId")`.
  - gRPC: `InvalidArgument` with the same message.
- Nothing is enqueued when a batch is rejected.

There's no shim for old clients. A client that sends a mixed batch gets the `400` and must split by
session, one call per session.

### Server code removed

Both endpoints become: validate, resolve one target, make one `Enqueue` call.

- **`QueueController.Enqueue`:**
  1. Validate each item's `sessionId` format, as today, and then that all items share the first
     item's `sessionId`.
  2. Remove the `GroupBy` loop and the running totals.
  3. Call the existing `EnqueueItemsAsync(queueId, ResolveTargetActorId(queueId, items[0].SessionId),
     actorItems)`, the same path `enqueue-object` already uses. The result and error mapping come
     with it.
- **`DaprMQGrpcService.Enqueue`:** the same session check, then one `Enqueue` call to
  `ResolveTargetActorId(request.QueueId, sessionId of the first item)`. Remove the `GroupBy` loop and
  the running totals.
- Remove the "mixed-session batch costs one Enqueue per distinct session" comments in both files.

Nothing changes in `QueueActor`, `SessionCoordinatorActor` or the session directory: they already see
one target per call.

### SDKs

No API change. Each SDK's enqueue already sends `sessionId` on each item, and now the server rejects a
mixed batch. Update each SDK's doc comment on the item's session field (.NET `EnqueueItemDto`, Go
`EnqueueItem.SessionID`, Python `EnqueueItem.session_id`, TypeScript enqueue item, Java `EnqueueItem`)
to say that all items in one call must share it. Any SDK test that sends a mixed batch changes to one
call per session.

### Pubsub component

Check `pubsub.daprmq` (in its own repository) for any path that sends several messages with different
session ids in one enqueue, such as bulk publish. If it has one, it must split by session before
calling enqueue.

### Docs

- [API_REFERENCE.md](../docs/API_REFERENCE.md): replace "A single request can mix items with
  different (or no) `sessionId`; each distinct target is enqueued to in one batched call" with the
  rule and the `400`.
- [design/sessions.md](../docs/design/sessions.md): note that one enqueue targets one session.
- [scheduled-delivery.md](scheduled-delivery.md): drop the per-session split from its "Mixed batches"
  section.

### Not changed

- `enqueue-object`, which already takes one `session-id` header per call.
- Session lease operations (accept, renew, release) and the `lease-id` header on dequeue,
  acknowledge and the other operations on a held lock.
- Topic publish. Its request reuses `ApiEnqueueItem`, so it accepts a per-item `sessionId` and silently
  drops it, because topics don't support sessions. That's a separate fix: reject a `sessionId` on
  publish.

## Tests (TDD order, per CLAUDE.md)

1. **Integration** ([SessionTests.cs](../server/tests/DaprMQ.IntegrationTests/Tests/SessionTests.cs)):
   - A mixed-session batch gets `400`, and nothing is enqueued to the plain queue or to either session.
   - A single-session batch still reaches its session.
2. **Controller**
   ([QueueControllerSessionTests.cs](../server/tests/DaprMQ.Tests/QueueControllerSessionTests.cs)):
   - Replace `Enqueue_MixedSessionAndPlainItems_IssuesOneCallPerTarget` with tests that these return
     `400` with no actor call:
     - two different session ids;
     - session and no-session items.
   - A same-session batch makes exactly one `Enqueue` call, to `{queueId}-session-{sessionId}`.
   - The existing routing and `Enqueue_InvalidSessionId_Returns400` tests stay.
3. **gRPC**
   ([DaprMQGrpcServiceSessionTests.cs](../server/tests/DaprMQ.Tests/DaprMQGrpcServiceSessionTests.cs)):
   the same, with `InvalidArgument`, replacing its `Enqueue_MixedSessionAndPlainItems_IssuesOneCallPerTarget`.
4. **SDKs:** only tests that sent a mixed batch change.

Run `dotnet test` in `server/tests/DaprMQ.Tests/` after each step, and `./build-and-test.sh` before
commit.

## Phases

1. Server: REST, gRPC, tests.
2. SDK doc comments, the pubsub component check, and docs.

Scheduled delivery work starts after this lands.
