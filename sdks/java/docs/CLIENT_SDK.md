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

`DaprMQClient` is REST-backed for every operation except `consumeSession`, which is the one method built on the `ConsumeSession` gRPC streaming RPC - everything else (`enqueue`, `dequeueLocked`, `acknowledge`, `extendLock`, `deadLetter`, `acceptSession`, `renewSessionLease`, `releaseSession`) is a plain HTTP call under the hood.

`close()` only shuts down the gRPC channel if `create(...)` built it; a channel you constructed and passed in yourself is left alone. Every method blocks the calling thread (there's no async/`Future` variant) except `consumeSession`, which hands back a lazily-pulled `SessionStream`.

Errors map to typed exceptions under `com.daprmq.client.errors` (`LockNotFoundException`, `LockExpiredException`, `SessionNotFoundException`, `SessionLockedException`, `SessionLeaseExpiredException`, `InvalidLeaseIdException`, `SessionActorUnavailableException`, `NoSessionsAvailableException`, `SessionLostException`, `ValidationException`, `ActorNotFoundException`), all extending `DaprMQException`. A `204 No Content` (queue empty / no session available) is not an error - `dequeueLocked`/`acceptSession` return `null` instead of throwing.

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
        .prefetchCount(10)
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

`SessionMessageContext` deliberately has no `leaseId` - the `ConsumeSession` wire protocol never exposes one to the client (the server tracks it internally and applies it when calling `acknowledge`/`deadLetter` on your behalf), which is exactly what makes the managed loop simpler than the manual API above.

Sticky routing to one specific session: set `targetSessionId(...)` alongside `maxConcurrentSessions(1)` (any other combination throws `IllegalArgumentException` - a targeted claim only ever occupies one slot).

On a failed claim (no session currently available), a slot backs off with a doubling/cap/reset-on-success shape (`minBackoffSeconds` → `maxBackoffSeconds`, reset to `minBackoffSeconds` the moment a claim succeeds).

### Why one shared client

**All of a `SessionQueueConsumer`'s `maxConcurrentSessions` slots share the single `DaprMQClient` (and its one gRPC channel) passed into its constructor** - never construct a separate `DaprMQClient` per slot. A gRPC channel multiplexes every RPC, including long-lived streams, over one (or a small number of, if the server's per-connection stream cap is exceeded) underlying HTTP/2 connection - this is what makes running 10s-100s of concurrent `consumeSession` streams cheap. Giving each slot its own channel would open that many separate HTTP/2 connections for no benefit - see the "multiplexing many sessions over one stream" discussion in [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the full reasoning.

## Testing your own code against this SDK

`SessionQueueConsumer` depends only on `SessionCapableClient` (the one-method `consumeSession` slice of `DaprMQClient`) rather than the concrete class, so it can be driven against a fake in tests without a live server. See `sdks/java/src/test/java/com/daprmq/client/FakeRequestObserver.java` and `SessionStreamTest`/`SessionQueueConsumerTest` for the pattern this SDK's own test suite uses.
