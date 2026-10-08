# daprmq-client (Java)

A Java client for DaprMQ's HTTP/gRPC API - `DaprMQClient` for direct queue and session operations, plus `SessionQueueConsumer`, a managed multi-session consume loop built on the `ConsumeSession` streaming RPC. Lives at `sdks/java/`, physically separate from the server codebase under `dotnet/`. Built on `java.net.http.HttpClient` (REST) and `grpc-java` (streaming); requires Java 17+.

## Install / Reference

Not yet published to Maven Central. Reference the module directly:

```xml
<dependency>
  <groupId>com.daprmq</groupId>
  <artifactId>daprmq-client</artifactId>
  <version>0.1.0</version>
</dependency>
```

```bash
cd sdks/java && mvn install
```

The gRPC stubs are generated at build time from the shared proto contract (`dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto` - the same source of truth the .NET, TypeScript, and Python SDKs use) via `protobuf-maven-plugin`; nothing to regenerate by hand, `mvn compile` does it.

## Constructing a client

```java
import com.daprmq.client.DaprMQClient;

// 1. Convenience factory - builds and owns a plaintext gRPC channel, closed by close().
DaprMQClient client = DaprMQClient.create("http://localhost:8002", "localhost:8003");

// 2. DI-friendly constructor - you own the ManagedChannel's lifecycle.
DaprMQClient client = new DaprMQClient("http://localhost:8002", myManagedChannel);

// ... when you're done with it (only closes what it owns - see below) ...
client.close();
```

`DaprMQClient` is REST-backed for every operation except `consumeSession`, which is the one method built on the `ConsumeSession` gRPC streaming RPC - everything else (`enqueue`, `dequeueLocked`, `acknowledge`, `acknowledgeBatch`, `extendLock`, `nack`, `deadLetter`, `acceptSession`, `renewSessionLease`, `releaseSession`) is a plain HTTP call under the hood.

`close()` only shuts down the gRPC channel if `create(...)` built it; a channel you constructed and passed in yourself is left alone. Every method blocks the calling thread (there's no async/`Future` variant) except `consumeSession`, which hands back a lazily-pulled `SessionStream`.

Errors map to typed exceptions under `com.daprmq.client.errors` (`LockNotFoundException`, `LockExpiredException`, `SessionNotFoundException`, `SessionLockedException`, `SessionLeaseExpiredException`, `InvalidLeaseIdException`, `SessionActorUnavailableException`, `NoSessionsAvailableException`, `SessionLostException`, `ValidationException`, `ActorNotFoundException`, `DaprMQUnavailableException`, `DeliveryUnknownException`), all extending `DaprMQException`. A `204 No Content` (queue empty / no session available) is not an error - `dequeueLocked`/`acceptSession` return `null` instead of throwing.

## Retries, failures and waiting for a server

Calls ride out a DaprMQ that briefly can't serve them, such as a worker restarting, under the shared
contract in [RETRIES_AND_READINESS.md](../../testing/RETRIES_AND_READINESS.md). [docs/TIMEOUTS_AND_RETRIES.md](../../../docs/TIMEOUTS_AND_RETRIES.md) covers what to program for, across all SDKs:

```java
DaprMQClient client = DaprMQClient.create(httpBaseUrl, grpcTarget, RetryOptions.defaults()
        .withTimeout(Duration.ofSeconds(30))   // default: how long to keep retrying an outage. ZERO = off
        .withAutoIdempotencyKeys(true));       // optional: makes every enqueue safe to retry
```

- **Slow is not failed.** A call that reached a busy queue waits its turn for as long as it takes, up to your own cancellation (and a 100 s per-call safety limit). The retry timeout never cuts it short.
- **Certainly not performed** (the server reports it couldn't serve the call, or the connection was
  refused): retried until the timeout, then `DaprMQUnavailableException`. Always safe to repeat later.
- **Outcome unknown** (the connection broke after sending, or no response arrived in time): an
  `enqueue` whose items all have an `idempotencyKey`, and `acknowledgeBatch`, are retried; anything else throws
  `DeliveryUnknownException` straight away. It carries `getOperation()`, `getQueueId()` and, for
  enqueue, `getIdempotencyKeys()`. What to do next:
  - **Enqueue without keys:** re-send and accept a possible duplicate.
  - **`dequeueLocked`:** don't re-send. If it ran, the items come back when their locks expire.
  - **acknowledge / extendLock / deadLetter / nack:** re-sending is safe in effect. A
    `LockNotFoundException` then means the first attempt worked.
- **Interrupting the calling thread** stops retrying with a `CancellationException`, and the
  interrupt flag stays set.

`client.waitForReady(Duration.ofSeconds(30))` waits until queue operations can be served (gRPC health
service `daprmq.DaprMQ.operations`), without writing anything. It returns `false` if the timeout runs
out first. `waitForReady()` waits until interrupted, and `waitForReady("daprmq.DaprMQ", timeout)`
waits only for the server instance itself.

## Basic queue operations

```java
import com.daprmq.client.types.EnqueueItem;

client.enqueue("my-queue", List.of(new EnqueueItem(Map.of("task", "send_email"))));

var result = client.dequeueLocked("my-queue", 1, 60, null);
if (result != null) {
    for (var item : result.items()) {
        // ... process item.item() (a Jackson JsonNode) ...
        client.acknowledge("my-queue", item.lockId());
    }
}
```

**Competing consumers.** By default a queue serves one lock at a time - while any item is locked, further locked dequeues come back `locked` (HTTP 423). When several consumers share a queue (e.g. replicas scaled out by KEDA), pass the `dequeueLocked(queueId, count, ttlSeconds, leaseId, true)` overload so each can hold its own locks concurrently.

## Sessions - manual API

For sticky routing, admin tooling, or callers who don't want a managed consume loop:

```java
var lease = client.acceptSession("my-queue", "order-42", 30);
if (lease != null) {
    var dequeued = client.dequeueLocked("my-queue-session-" + lease.sessionId(), 1, 30, lease.leaseId());
    // ... acknowledge/extendLock/deadLetter against the same derived queue id + leaseId ...
    client.releaseSession("my-queue", lease.sessionId(), lease.leaseId());
}
```

See [API_REFERENCE.md](../../../docs/API_REFERENCE.md#sessions) and [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the underlying actor-id convention and lease semantics.

## Sessions - managed consume loop (`SessionQueueConsumer`)

`SessionQueueConsumer` is the recommended way to consume sessions: it runs `maxConcurrentSessions` independent slots, each looping over its own `consumeSession` stream - claim a session, hand each delivered item to your handler, ack or dead-letter it, repeat until the session drains, then claim another. No manual lease heartbeating is needed; the server renews the lease on its own schedule for as long as the stream stays open.

```java
import com.daprmq.client.SessionHandlerFailureAction;
import com.daprmq.client.SessionQueueConsumer;
import com.daprmq.client.SessionQueueConsumerOptions;

var options = new SessionQueueConsumerOptions()
        .maxConcurrentSessions(8)
        .leaseSeconds(30)
        .prefetchCount(1)
        .onHandlerException(SessionHandlerFailureAction.DEAD_LETTER_MESSAGE);

var consumer = new SessionQueueConsumer(client, "my-queue", options, context -> {
    // context.sessionId(), context.lockId(), context.item(), context.priority()
    process(context.item());
    // returning normally acks the item; throwing triggers onHandlerException's behavior
});

consumer.start();
// ... run your application ...
consumer.stop(); // stops claiming, cancels blocked streams, drains in-flight handlers
```

`onHandlerException` is one of `DEAD_LETTER_MESSAGE` (default), `NACK_MESSAGE` (return the item to the front of the session for redelivery - counts toward the server's max delivery count, past which it is dead-lettered), `ABANDON_SESSION`, or `BOTH`. Session ordering holds only at the default prefetch of 1: with a larger prefetch, items already delivered are handled before a nacked item comes back.

`acknowledgeBatch(queueId, lockIds[, leaseId])` settles up to 1,000 locks in one call (one actor turn, one state save), typically everything a bulk `dequeueLocked` with `count` returned. It returns `AcknowledgeBatchResult(itemsAcknowledged, results)` with one `LockAcknowledgeResult(lockId, outcome)` per lock in request order; `outcome` is one of the `AcknowledgeOutcome` constants (`ACKNOWLEDGED`, `LOCK_NOT_FOUND`, `LOCK_EXPIRED`, `INVALID_LOCK_ID`). A lock that can't be settled never fails the others; only a whole-call problem throws (`SessionLeaseExpiredException`, `InvalidLeaseIdException`, `ValidationException` for an empty list, more than 1,000 ids or duplicates). An unknown outcome is retried automatically, so after a retry `LOCK_NOT_FOUND` can mean the first attempt already settled that lock.

`nack(queueId, lockId[, leaseId])` on the client (and `nack()` on a `SessionDelivery`) returns a locked item to its original position; it returns `NackResult(deadLettered, deliveryCount, dlqId)`.

`SessionMessageContext` deliberately has no `leaseId` - the `ConsumeSession` wire protocol never exposes one to the client (the server tracks it internally and applies it when calling `acknowledge`/`deadLetter` on your behalf), which is exactly what makes the managed loop simpler than the manual API above.

Sticky routing to one specific session: set `targetSessionId(...)` alongside `maxConcurrentSessions(1)` (any other combination throws `IllegalArgumentException` - a targeted claim only ever occupies one slot).

On a failed claim (no session currently available), a slot backs off with a doubling/cap/reset-on-success shape (`minBackoffSeconds` → `maxBackoffSeconds`, reset to `minBackoffSeconds` the moment a claim succeeds).

### Why one shared client

**All of a `SessionQueueConsumer`'s `maxConcurrentSessions` slots share the single `DaprMQClient` (and its one gRPC channel) passed into its constructor** - never construct a separate `DaprMQClient` per slot. A gRPC channel multiplexes every RPC, including long-lived streams, over one (or a small number of, if the server's per-connection stream cap is exceeded) underlying HTTP/2 connection - this is what makes running 10s-100s of concurrent `consumeSession` streams cheap. Giving each slot its own channel would open that many separate HTTP/2 connections for no benefit - see the "multiplexing many sessions over one stream" discussion in [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the full reasoning.

## Testing your own code against this SDK

`SessionQueueConsumer` depends only on `SessionCapableClient` (the one-method `consumeSession` slice of `DaprMQClient`) rather than the concrete class, so it can be driven against a fake in tests without a live server. See `sdks/java/src/test/java/com/daprmq/client/FakeRequestObserver.java` and `SessionStreamTest`/`SessionQueueConsumerTest` for the pattern this SDK's own test suite uses.
