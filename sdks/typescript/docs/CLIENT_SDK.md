# daprmq-client (TypeScript)

A TypeScript/Node client for DaprMQ's HTTP/gRPC API - `DaprMQClient` for direct queue and session operations, plus `SessionQueueConsumer`, a managed multi-session consume loop built on the `ConsumeSession` streaming RPC. Lives at `sdks/typescript/`, physically separate from the server codebase under `dotnet/`.

## Install / Reference

Not yet published to npm. Reference the package directly (workspace or `file:` dependency):

```json
{
  "dependencies": {
    "daprmq-client": "file:../path/to/sdks/typescript"
  }
}
```

```bash
cd sdks/typescript && npm install && npm run build
```

## Constructing a client

```ts
import { DaprMQClient } from "daprmq-client";

const client = new DaprMQClient({
  httpBaseUrl: "http://localhost:8002",
  grpcAddress: "localhost:8003",
});

// ... when you're done with it (only closes what it owns - see below) ...
client.close();
```

`DaprMQClient` is REST-backed for every operation except `consumeSession`, which is the one method built on the `ConsumeSession` gRPC streaming RPC - everything else (`enqueue`, `dequeueLocked`, `acknowledge`, `extendLock`, `deadLetter`, `acceptSession`, `renewSessionLease`, `releaseSession`) is a plain `fetch` call under the hood.

`grpcAddress` is required unless you pass a pre-built `grpcClient` (e.g. in tests - see `fetch`/`grpcClient` in `DaprMQClientOptions`, both DI/test seams). `client.close()` only tears down the gRPC client if this instance built it; a `grpcClient` you supplied yourself is left alone.

Errors map to typed exceptions exported alongside the client (`LockNotFoundError`, `LockExpiredError`, `SessionNotFoundError`, `SessionLockedError`, `SessionLeaseExpiredError`, `InvalidLeaseIdError`, `SessionActorUnavailableError`, `NoSessionsAvailableError`, `SessionLostError`, `ValidationError`, `ActorNotFoundError`), all deriving from `DaprMQError`. A `204 No Content` (queue empty / no session available) is not an error - `dequeueLocked`/`acceptSession` resolve to `null` instead of throwing.

## Basic queue operations

```ts
await client.enqueue("my-queue", [{ item: { task: "send_email" } }]);

const result = await client.dequeueLocked("my-queue", { ttlSeconds: 60 });
if (result) {
  for (const item of result.items) {
    // ... process item.item ...
    await client.acknowledge("my-queue", item.lockId);
  }
}
```

## Sessions - manual API

For sticky routing, admin tooling, or callers who don't want a managed consume loop:

```ts
const lease = await client.acceptSession("my-queue", { sessionId: "order-42" });
if (lease) {
  const dequeued = await client.dequeueLocked(`my-queue-session-${lease.sessionId}`, { leaseId: lease.leaseId });
  // ... acknowledge/extendLock/deadLetter against the same derived queue id + leaseId ...
  await client.releaseSession("my-queue", lease.sessionId, lease.leaseId);
}
```

See [API_REFERENCE.md](../../../docs/API_REFERENCE.md#sessions) and [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the underlying actor-id convention and lease semantics.

## Sessions - managed consume loop (`SessionQueueConsumer`)

`SessionQueueConsumer` is the recommended way to consume sessions: it runs `maxConcurrentSessions` independent slots, each looping over its own `consumeSession` stream - claim a session, hand each delivered item to your handler, ack or dead-letter it, repeat until the session drains, then claim another. No manual lease heartbeating is needed; the server renews the lease on its own schedule for as long as the stream stays open.

```ts
import { SessionQueueConsumer } from "daprmq-client";

const consumer = new SessionQueueConsumer(
  client,
  "my-queue",
  {
    maxConcurrentSessions: 8,
    leaseSeconds: 30,
    prefetchCount: 10,
    onHandlerException: "deadLetterMessage",
  },
  async (context, signal) => {
    // context.sessionId, context.lockId, context.item, context.priority
    await process(context.item, signal);
    // returning normally acks the item; throwing triggers onHandlerException's behavior
  },
);

consumer.start();
// ... run your application ...
await consumer.stop(); // stops claiming, drains in-flight handlers, closes streams
```

`SessionMessageContext` deliberately has no `leaseId` - the `ConsumeSession` wire protocol never exposes one to the client (the server tracks it internally and applies it when calling `acknowledge`/`deadLetter` on your behalf), which is exactly what makes the managed loop simpler than the manual API above.

Sticky routing to one specific session: set `targetSessionId` alongside `maxConcurrentSessions: 1` (any other combination throws - a targeted claim only ever occupies one slot).

On a failed claim (no session currently available), a slot backs off with a doubling/cap/reset-on-success shape (`minBackoffSeconds` → `maxBackoffSeconds`, reset to `minBackoffSeconds` the moment a claim succeeds).

### Why one shared gRPC client

**All of a `SessionQueueConsumer`'s `maxConcurrentSessions` slots share the single gRPC client owned by its `DaprMQClient`** - never construct a separate `DaprMQClient` per slot. The underlying `@grpc/grpc-js` client multiplexes every RPC, including long-lived streams, over one (or a small number of, if the server's per-connection stream cap is exceeded) underlying HTTP/2 connection - this is what makes running 10s-100s of concurrent `consumeSession` streams cheap. Giving each slot its own client would open that many separate HTTP/2 connections for no benefit - see the "multiplexing many sessions over one stream" discussion in [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the full reasoning.

## Testing your own code against this SDK

`DaprMQClientOptions.fetch` and `.grpcClient` are DI/test seams - substitute a fake `fetch` and a fake gRPC client to exercise code that depends on `DaprMQClient` without a live server. See `sdks/typescript/tests/fakeFetch.ts` and `sdks/typescript/tests/fakeGrpcClient.ts` for the pattern this SDK's own test suite uses.
