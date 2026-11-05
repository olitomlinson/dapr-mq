# One session per enqueue

**Status:** proposed. Not yet implemented. A prerequisite for
[scheduled-delivery.md](scheduled-delivery.md).

## Context

Today one enqueue call can mix items for different sessions, and items with no session. Both enqueue
endpoints group the items by target actor and make one actor `Enqueue` call per group:

- REST: `QueueController.Enqueue`
  ([QueueController.cs](../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs)), which groups
  by `ResolveTargetActorId(queueId, item.SessionId)`.
- gRPC: `DaprMQGrpcService.Enqueue`
  ([DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)), which does
  the same and is commented "Mirrors QueueController.Enqueue".

This has a problem: **a mixed batch is not atomic, and a failure doesn't say what happened.** Each
group commits on its own actor. If a later group fails, the endpoint returns an error after earlier
groups have already committed. The caller can't tell which items were enqueued. Retrying the whole
batch duplicates the committed groups at any target with dedup off, and without idempotency keys
dedup can't help at all. That falls short of the rule that a success response means the items were
processed and a failure means they weren't.

Azure Service Bus doesn't allow this either. Its SDK requires all messages in one send call to have
the same session id: "All messages passed to the same sendMessages() call should have the same
sessionId (if using sessions)"
([ServiceBusSender](https://learn.microsoft.com/en-us/javascript/api/@azure/service-bus/servicebussender)).

Scheduled delivery is also affected. Its `ScheduleActor` needs a clear outcome for each target it
delivers to, and one call per session gives it that.

## Proposal

**The session id moves from each item to the enqueue request.** One enqueue call targets exactly one
actor: the plain queue, or one session of it. A mixed batch can then no longer be expressed at all,
so no validation is needed, and the grouping code is removed.

This is preferred over keeping a session id on each item and rejecting batches where they differ,
because that would keep a field the server then has to check. A request-level field makes the rule
part of the API's shape. It also matches the shape `enqueue-object` already has: one `session-id`
header per call.

### API

**REST: `POST /queue/{queueId}/enqueue`**

```json
{ "sessionId": "order-42", "items": [ { "item": { … }, "priority": 1, "idempotencyKey": "…" } ] }
```

- `ApiEnqueueRequest` gains `string? SessionId`. `ApiEnqueueItem` loses `SessionId`.
- `sessionId` is validated once per request, with the existing `IsValidSessionId`.
- `enqueue-object` is unchanged: it already takes a single `session-id` header.

**Topic publish: `POST /topic/{topicId}/publish`**

`ApiPublishRequest` currently reuses `ApiEnqueueItem`, so it accepts a per-item `sessionId` and
silently drops it: `TopicController.Publish` never reads it. Topics don't support sessions. Once
`SessionId` is removed from `ApiEnqueueItem`, publish no longer carries the field. No other change is
needed.

**gRPC** ([daprmq.proto](../server/src/DaprMQ.ApiServer/Protos/daprmq.proto))

- `EnqueueRequest` gains `optional string session_id = 3`.
- `EnqueueItem.session_id` (field 4) is removed, and the field is marked `reserved 4;` so the number
  isn't reused.

### Server code removed

Both endpoints become a single target resolve plus a single `Enqueue`:

- **`QueueController.Enqueue`:** remove the `GroupBy` loop and the running totals, and validate
  `sessionId` once. Then call the existing `EnqueueItemsAsync(queueId, ResolveTargetActorId(queueId,
  request.SessionId), items)`, the same path `enqueue-object` already uses. The result and error
  mapping come with it.
- **`DaprMQGrpcService.Enqueue`:** remove the `GroupBy` loop and the running totals. Make one
  `Enqueue` call to `ResolveTargetActorId(request.QueueId, request.SessionId)`.
- Remove the "mixed-session batch costs one Enqueue per distinct session" comments in both files.

Nothing changes in `QueueActor`, `SessionCoordinatorActor` or the session directory: they already
see one target per call.

### SDKs

All five SDKs move the session id from the enqueue item to an enqueue option:

| SDK | Removed from the item | Added to enqueue |
|---|---|---|
| .NET | `EnqueueItemDto.SessionId` | `sessionId` parameter or option |
| Go | `EnqueueItem.SessionID` (`types.go`, `enqueueWireItem` in `client.go`) | `SessionID` on the enqueue options |
| Python | `EnqueueItem.session_id` | `session_id` keyword argument |
| TypeScript | `sessionId` on the enqueue item (`types.ts`, `client.ts`) | `sessionId` in the enqueue options |
| Java | `EnqueueItem.sessionId` | `sessionId` parameter or option |

Each follows the SDK's existing pattern for optional enqueue settings. Generated protos are
regenerated (Go `generate.go`, TypeScript `proto/`, .NET at build).

Callers that enqueue for several sessions make one call per session, as with Azure Service Bus.

### Pubsub component

Check `pubsub.daprmq` (in its own repository) for any path that sends several messages with
different session ids in one enqueue, such as bulk publish. If it has one, it must split by session
before calling enqueue.

### Docs

- [API_REFERENCE.md](../docs/API_REFERENCE.md): move `sessionId` from the item list to the request.
  Delete the sentence "A single request can mix items with different (or no) `sessionId`; each
  distinct target is enqueued to in one batched call".
- [design/sessions.md](../docs/design/sessions.md): note that one enqueue targets one session.
- [scheduled-delivery.md](scheduled-delivery.md): its "Mixed batches" section is updated to drop the
  per-session split.

### Not changed

- `enqueue-object`, which already has one session per call.
- Session lease operations (accept, renew, release) and the `lease-id` header on dequeue, acknowledge
  and the other operations on a held lock.
- Pre-v1, there is no compatibility shim: an old client that sends a per-item `sessionId` loses its
  session routing. The JSON field is ignored on REST and the field number is reserved on gRPC.

## Tests (TDD order, per CLAUDE.md)

1. **Integration** ([SessionTests.cs](../server/tests/DaprMQ.IntegrationTests/Tests/SessionTests.cs)
   and the other integration tests that enqueue to sessions): move `sessionId` to the request. A
   session enqueue still reaches the session and is accepted after it.
2. **Controller**
   ([QueueControllerSessionTests.cs](../server/tests/DaprMQ.Tests/QueueControllerSessionTests.cs)):
   - Replace `Enqueue_MixedSessionAndPlainItems_IssuesOneCallPerTarget` with a test that a request
     with `sessionId` makes exactly one `Enqueue` call, to `{queueId}-session-{sessionId}`.
   - `Enqueue_WithSessionId_RoutesToSessionActorId`, `Enqueue_WithoutSessionId_RoutesToPlainQueueActorId`
     and `Enqueue_InvalidSessionId_Returns400` move to the request-level field.
   - An actor failure returns the error with nothing committed elsewhere, because there is one call.
3. **gRPC**
   ([DaprMQGrpcServiceSessionTests.cs](../server/tests/DaprMQ.Tests/DaprMQGrpcServiceSessionTests.cs)):
   the same changes, including replacing its `Enqueue_MixedSessionAndPlainItems_IssuesOneCallPerTarget`.
4. **SDKs:** each SDK's enqueue tests send `sessionId` at request level, and no item type has a
   session field.

Run `dotnet test` in `server/tests/DaprMQ.Tests/` after each step, and `./build-and-test.sh` before
commit.

## Phases

1. Server: REST, gRPC, proto, tests.
2. SDKs: all five.
3. Pubsub component check, then docs.

Scheduled delivery work starts after this lands.
