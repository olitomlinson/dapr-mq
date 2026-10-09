# DaprMQ.Client (.NET)

A .NET client for DaprMQ's HTTP/gRPC API - `IDaprMQClient`/`DaprMQClient` for direct queue and session operations, plus `SessionQueueConsumer`, a managed multi-session consume loop built on the `ConsumeSession` streaming RPC. Lives in its own solution at `sdks/dotnet/DaprMQ.Client.sln`, physically separate from the server codebase under `dotnet/` - a distributable client library has no business depending on ASP.NET Core hosting or the Dapr actor runtime.

See also: [Cluster Discovery](CLUSTER_DISCOVERY.md) - finding `HttpBaseAddress`/`GrpcAddress` when the target is a `daprmq` Helm release.

## Install / Reference

Not yet published as a NuGet package. Reference the project directly:

```bash
dotnet add reference path/to/sdks/dotnet/src/DaprMQ.Client/DaprMQ.Client.csproj
```

## Constructing a client

Two ways to build a `DaprMQClient`:

```csharp
// 1. Convenience constructor - builds and owns both the HttpClient and GrpcChannel.
var client = new DaprMQClient(new DaprMQClientOptions
{
    HttpBaseAddress = new Uri("http://localhost:8002/"),
    GrpcAddress = "http://localhost:8003"
});

// 2. DI-friendly constructor - you own the lifetime of both.
var client = new DaprMQClient(httpClient, grpcChannel);
```

`IDaprMQClient` is REST-backed for every operation except `ConsumeSessionAsync`, which is the one method built on the `ConsumeSession` gRPC streaming RPC - everything else (`Enqueue`, `DequeueLocked`, `Acknowledge`, `AcknowledgeBatch`, `ExtendLock`, `Nack`, `DeadLetter`, `AcceptSession`, `RenewSessionLease`, `ReleaseSession`) is a plain HTTP call under the hood.

Errors map to typed exceptions under `DaprMQ.Client.Exceptions` (`LockNotFoundException`, `LockExpiredException`, `SessionNotFoundException`, `SessionLockedException`, `SessionLeaseExpiredException`, `InvalidLeaseIdException`, `SessionActorUnavailableException`, `NoSessionsAvailableException`, `SessionLostException`, `ValidationException`, `ActorNotFoundException`, `DaprMQUnavailableException`, `DeliveryUnknownException`), all deriving from `DaprMQException`. A `204 No Content` (queue empty / no session available) is not an error - `DequeueLockedAsync`/`AcceptSessionAsync` return `null` instead of throwing.

## Retries, failures and waiting for a server

Calls ride out a DaprMQ that briefly can't serve them, such as a worker restarting, under the shared
contract in [RETRIES_AND_READINESS.md](../../testing/RETRIES_AND_READINESS.md). [docs/TIMEOUTS_AND_RETRIES.md](../../../docs/TIMEOUTS_AND_RETRIES.md) covers what to program for, across all SDKs:

```csharp
var client = new DaprMQClient(httpClient, grpcChannel, new DaprMQRetryOptions
{
    Timeout = TimeSpan.FromSeconds(30),   // default: how long to keep retrying an outage. Zero = off
    AutoIdempotencyKeys = true,           // optional: makes every enqueue safe to retry
});
// or: new DaprMQClientOptions { HttpBaseAddress = ..., GrpcAddress = ..., Retry = new() { ... } }
```

- **Slow is not failed.** A call that reached a busy queue waits its turn for as long as it takes, up to your own cancellation (and a 100 s per-call safety limit). The retry timeout never cuts it short.
- **Certainly not performed** (the server reports it couldn't serve the call, or the connection was
  refused): retried until `Timeout`, then `DaprMQUnavailableException`. Always safe to repeat later.
- **Outcome unknown** (the connection broke after sending, or no response arrived in time): an
  `EnqueueAsync` whose items all have an `IdempotencyKey`, and `AcknowledgeBatchAsync`, are retried; anything else throws
  `DeliveryUnknownException` straight away. It carries the `Operation`, the `QueueId` and, for
  Enqueue, the items' `IdempotencyKeys`. What to do next:
  - **Enqueue without keys:** re-send and accept a possible duplicate.
  - **`DequeueLockedAsync`:** don't re-send. If it ran, the items come back when their locks expire.
  - **Acknowledge / ExtendLock / DeadLetter / Nack:** re-sending is safe in effect. A `LockNotFoundException`
    then means the first attempt worked.
- **Cancelling your token** stops retrying and surfaces as `OperationCanceledException`.

`WaitForReadyAsync()` waits until queue operations can be served (gRPC health service
`daprmq.DaprMQ.operations`), without writing anything. Bound it with your token. Pass
`"daprmq.DaprMQ"` to wait only for the server instance itself.

## Basic queue operations

```csharp
await client.EnqueueAsync("my-queue", [new EnqueueItemDto(new { task = "send_email" })]);

var result = await client.DequeueLockedAsync("my-queue", ttlSeconds: 60);
if (result is not null)
{
    foreach (var item in result.Items)
    {
        // ... process item.Item (a JsonElement) ...
        await client.AcknowledgeAsync("my-queue", item.LockId);
    }
}
```

**Competing consumers.** By default a queue serves one lock at a time - while any item is locked, further locked dequeues come back `locked` (HTTP 423). When several consumers share a queue (e.g. replicas scaled out by KEDA), pass `allowCompetingConsumers: true` so each can hold its own locks concurrently.

## Consuming a queue over a stream (`ConsumeAsync`, `QueueConsumer`)

`ConsumeAsync` opens a gRPC stream on a plain queue. The server keeps up to `PrefetchCount` locked items delivered, refills as they are settled, and renews their locks until they are settled, so there's no polling and no `ExtendLockAsync`:

```csharp
var options = new ConsumeOptions
{
    PrefetchCount = 50,
    AllowCompetingConsumers = true,
    OnSettleFailed = (lockId, error) => logger.LogWarning("settle {LockId}: {Error}", lockId, error.Message)
};
await foreach (var delivery in client.ConsumeAsync("my-queue", options, ct))
{
    // ... process delivery.Item; delivery.DeliveryCount is 1 on a first delivery ...
    await delivery.AckAsync(ct); // or NackAsync / DeadLetterAsync
}
```

A rejected settle doesn't end the stream: it goes to `OnSettleFailed`. Leaving the loop, or cancelling `ct`, half-closes the stream: the server applies the settles already sent and returns every unsettled item straight away. Settling afterwards throws `StreamClosedException`.

`QueueConsumer` is the recommended way to run a long-lived consumer. It runs your handler over the stream and reopens it with backoff if it breaks:

```csharp
await using var consumer = new QueueConsumer(client, "my-queue", new QueueConsumerOptions { MaxConcurrentHandlers = 10 },
    async (ctx, ct) =>
    {
        // ctx.LockId, ctx.Item, ctx.Priority, ctx.DeliveryCount
        await ProcessAsync(ctx.Item, ct); // returning acks; throwing applies OnHandlerError
    });
await consumer.StartAsync();
// ... run your application ...
await consumer.StopAsync();
```

Stopping - `StopAsync()` - stops handing out messages, lets running handlers finish and settle for up to `DrainTimeout`, then closes the stream, so the server returns every message the consumer held but hadn't started straight away. For a job that takes a batch and exits (a KEDA ScaledJob, a cron job), use `DequeueLockedAsync` instead: a stream's prefetch would lock messages the job never handles.

| Option | Default | Notes |
| --- | --- | --- |
| `MaxActiveMessages` | 100 | The stream's prefetch count: messages locked to this consumer, handled or waiting |
| `MaxConcurrentHandlers` | 0 (unlimited) | Bounded by `MaxActiveMessages` |
| `LockTtl` | 30 s | The server renews each lock until its message is settled |
| `AllowCompetingConsumers` | `true` | Replicas share the queue |
| `StrictOrder` | `false` | Queue order even after a nack: a window of 1, one handler, no competing consumers |
| `OnHandlerError` | `Nack` | Or `DeadLetter` |
| `MaxRetriableErrorsPerSec` | 10 | Paces nacks after handler errors; 0 for unpaced |
| `MinBackoffSeconds` / `MaxBackoffSeconds` | 1 / 60 | Seconds before reopening a broken stream; doubles per break, resets after a delivery |
| `DrainTimeout` | 30 s | |
## Sessions - manual (unary) API

For sticky routing, admin tooling, or callers who don't want a managed consume loop:

```csharp
var lease = await client.AcceptSessionAsync("my-queue", sessionId: "order-42");
if (lease is not null)
{
    var dequeued = await client.DequeueLockedAsync($"my-queue-session-{lease.SessionId}", leaseId: lease.LeaseId);
    // ... acknowledge/extend/dead-letter against the same derived queue id + leaseId ...
    await client.ReleaseSessionAsync("my-queue", lease.SessionId, lease.LeaseId);
}
```

See [API_REFERENCE.md](../../../docs/API_REFERENCE.md#sessions) and [design/sessions.md](../../../docs/design/sessions.md) for the underlying actor-id convention and lease semantics.

## Sessions - managed consume loop (`SessionQueueConsumer`)

`SessionQueueConsumer` is the recommended way to consume sessions: it runs `MaxConcurrentSessions` independent slots, each looping over its own `ConsumeSession` stream - claim a session, hand each delivered item to your handler, ack or dead-letter it, repeat until the session drains, then claim another. No manual lease heartbeating is needed; the server renews the lease on its own schedule for as long as the stream stays open.

```csharp
var options = new SessionQueueConsumerOptions
{
    MaxConcurrentSessions = 8,
    LeaseSeconds = 30,
    PrefetchCount = 1,
    OnHandlerException = SessionHandlerFailureAction.DeadLetterMessage
};

await using var consumer = new SessionQueueConsumer(client, "my-queue", options, async (context, ct) =>
{
    // context.SessionId, context.LockId, context.Item (JsonElement), context.Priority
    await ProcessAsync(context.Item, ct);
    // returning normally acks the item; throwing triggers OnHandlerException's behavior
});

await consumer.StartAsync();
// ... run your application ...
await consumer.StopAsync(); // stops claiming, lets in-flight handlers settle, closes streams
```

`StopAsync` stops claiming sessions and closes idle streams at once. A handler that is running finishes and its message is settled (acked, or per `OnHandlerException`) before its stream closes; prefetched messages not yet handled return with their session. The handler's token is cancelled only if handlers are still running after `DrainTimeout` (default 30 s). `StopAsync` returns once every stream has closed, so the sessions are released by then. Cancelling the token passed to `StartAsync` stops the consumer the same way. Closing a `ConsumeSessionAsync` stream (cancelling its token, or leaving the `await foreach`) half-closes it: settlements already sent are applied before the session is released, and the call is cancelled only if the server hasn't ended it within 5 s.

`OnHandlerException` is one of `DeadLetterMessage` (default), `NackMessage` (return the item to the front of the session for redelivery - counts toward the server's max delivery count, past which it is dead-lettered), `AbandonSession`, or `Both`. Session ordering holds only at the default prefetch of 1: with a larger prefetch, items already delivered are handled before a nacked item comes back.

`AcknowledgeBatchAsync(queueId, lockIds, leaseId?)` settles up to 1,000 locks in one call (one actor turn, one state save), typically everything a bulk `DequeueLockedAsync(count: n)` returned. It returns `AcknowledgeBatchResult(ItemsAcknowledged, Results)` with one `LockAcknowledgeResult(LockId, Outcome)` per lock in request order; `Outcome` is one of the `AcknowledgeOutcomes` constants (`ACKNOWLEDGED`, `LOCK_NOT_FOUND`, `LOCK_EXPIRED`, `INVALID_LOCK_ID`). A lock that can't be settled never fails the others; only a whole-call problem throws (`SessionLeaseExpiredException`, `InvalidLeaseIdException`, `ValidationException` for an empty list, more than 1,000 ids or duplicates). An unknown outcome is retried automatically, so after a retry `LOCK_NOT_FOUND` can mean the first attempt already settled that lock.

`NackAsync(queueId, lockId, leaseId?)` on the client (and `NackAsync` on a `SessionDelivery`) returns a locked item to its original position; it returns `NackResult(DeadLettered, DeliveryCount, DlqId)`.

`SessionMessageContext` deliberately has no `LeaseId` - the `ConsumeSession` wire protocol never exposes one to the client (the server tracks it internally and applies it when calling `Acknowledge`/`DeadLetter` on your behalf), which is exactly what makes the managed loop simpler than the manual API above.

Sticky routing to one specific session: set `TargetSessionId` alongside `MaxConcurrentSessions = 1` (any other combination throws `ArgumentException` - a targeted claim only ever occupies one slot).

On a failed claim (no session currently available), a slot backs off with the same doubling/cap/reset-on-success shape `HttpSinkActor` uses for its own empty-queue polling (`MinBackoffSeconds` → `MaxBackoffSeconds`, reset to `MinBackoffSeconds` the moment a claim succeeds).

### Why one shared `GrpcChannel`

**All of a `SessionQueueConsumer`'s `MaxConcurrentSessions` slots share the single `GrpcChannel` passed into (or built by) its `DaprMQClient`** - never construct a separate `DaprMQClient`/`GrpcChannel` per slot. A `GrpcChannel` multiplexes every RPC, including long-lived streams, over one (or a small number of, if the server's per-connection stream cap is exceeded) underlying HTTP/2 connection - this is what makes running 10s-100s of concurrent `ConsumeSession` streams cheap. Giving each slot its own channel would open that many separate HTTP/2 connections for no benefit, and was considered and rejected during the sessions feature's design - see the "multiplexing many sessions over one stream" discussion in [design/sessions.md](../../../docs/design/sessions.md) for the full reasoning (the conclusion there was the mirror image of this point: many streams over one connection is fine; it's one connection per stream that would be wasteful).
