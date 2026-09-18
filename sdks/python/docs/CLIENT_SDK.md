# daprmq-client (Python)

An async Python client for DaprMQ's HTTP/gRPC API - `DaprMQClient` for direct queue and session operations, plus `SessionQueueConsumer`, a managed multi-session consume loop built on the `ConsumeSession` streaming RPC. Lives at `sdks/python/`, physically separate from the server codebase under `dotnet/`. Built on `httpx` (REST) and `grpc.aio` (streaming) - every public method is a coroutine.

## Install / Reference

Not yet published to PyPI. Install from the local path (editable, for development):

```bash
uv pip install -e path/to/sdks/python
```

or add it as a path dependency in your own `pyproject.toml`:

```toml
[project]
dependencies = ["daprmq-client @ file:///path/to/sdks/python"]
```

The gRPC stubs under `daprmq_client/grpc/` are generated from the shared proto contract (`dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto` - the same source of truth the .NET and TypeScript SDKs use) and checked in; re-run `python scripts/generate_grpc.py` from `sdks/python/` after the proto changes.

## Constructing a client

```python
from daprmq_client import DaprMQClient

async with DaprMQClient(
    http_base_url="http://localhost:8002",
    grpc_address="localhost:8003",
) as client:
    ...  # client.aclose() runs automatically on exit
```

or without the context manager:

```python
client = DaprMQClient(http_base_url="http://localhost:8002", grpc_address="localhost:8003")
try:
    ...
finally:
    await client.aclose()
```

`DaprMQClient` is REST-backed for every operation except `consume_session`, which is the one method built on the `ConsumeSession` gRPC streaming RPC - everything else (`enqueue`, `dequeue_locked`, `acknowledge`, `extend_lock`, `dead_letter`, `accept_session`, `renew_session_lease`, `release_session`) is a plain HTTP call under the hood (via `httpx.AsyncClient`).

`grpc_address` is required unless you pass a pre-built `grpc_stub` (e.g. in tests - `http_client`/`grpc_stub` are both DI/test seams). `aclose()` only tears down the `httpx.AsyncClient`/gRPC channel this instance built itself; an `http_client` or `grpc_stub` you supplied yourself is left alone.

Errors map to typed exceptions exported alongside the client (`LockNotFoundError`, `LockExpiredError`, `SessionNotFoundError`, `SessionLockedError`, `SessionLeaseExpiredError`, `InvalidLeaseIdError`, `SessionActorUnavailableError`, `NoSessionsAvailableError`, `SessionLostError`, `ValidationError`, `ActorNotFoundError`), all deriving from `DaprMQError`. A `204 No Content` (queue empty / no session available) is not an error - `dequeue_locked`/`accept_session` return `None` instead of raising.

## Basic queue operations

```python
from daprmq_client import EnqueueItem

await client.enqueue("my-queue", [EnqueueItem(item={"task": "send_email"})])

result = await client.dequeue_locked("my-queue", ttl_seconds=60)
if result is not None:
    for item in result.items:
        # ... process item.item ...
        await client.acknowledge("my-queue", item.lock_id)
```

## Sessions - manual API

For sticky routing, admin tooling, or callers who don't want a managed consume loop:

```python
lease = await client.accept_session("my-queue", session_id="order-42")
if lease is not None:
    dequeued = await client.dequeue_locked(f"my-queue-session-{lease.session_id}", lease_id=lease.lease_id)
    # ... acknowledge/extend_lock/dead_letter against the same derived queue id + lease_id ...
    await client.release_session("my-queue", lease.session_id, lease.lease_id)
```

See [API_REFERENCE.md](../../../docs/API_REFERENCE.md#sessions) and [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the underlying actor-id convention and lease semantics.

## Sessions - managed consume loop (`SessionQueueConsumer`)

`SessionQueueConsumer` is the recommended way to consume sessions: it runs `max_concurrent_sessions` independent slots, each looping over its own `consume_session` stream - claim a session, hand each delivered item to your handler, ack or dead-letter it, repeat until the session drains, then claim another. No manual lease heartbeating is needed; the server renews the lease on its own schedule for as long as the stream stays open.

```python
from daprmq_client import SessionHandlerFailureAction, SessionQueueConsumer, SessionQueueConsumerOptions

options = SessionQueueConsumerOptions(
    max_concurrent_sessions=8,
    lease_seconds=30,
    prefetch_count=10,
    on_handler_exception=SessionHandlerFailureAction.DEAD_LETTER_MESSAGE,
)

async def handler(context):
    # context.session_id, context.lock_id, context.item, context.priority
    await process(context.item)
    # returning normally acks the item; raising triggers on_handler_exception's behavior

async with SessionQueueConsumer(client, "my-queue", options, handler):
    ...  # consumer.start() ran on entry; consumer.stop() runs on exit
```

or without the context manager:

```python
consumer = SessionQueueConsumer(client, "my-queue", options, handler)
consumer.start()
# ... run your application ...
await consumer.stop()  # stops claiming, drains in-flight handlers, closes streams
```

`SessionMessageContext` deliberately has no `lease_id` - the `ConsumeSession` wire protocol never exposes one to the client (the server tracks it internally and applies it when calling `acknowledge`/`dead_letter` on your behalf), which is exactly what makes the managed loop simpler than the manual API above.

Sticky routing to one specific session: set `target_session_id` alongside `max_concurrent_sessions=1` (any other combination raises `ValueError` - a targeted claim only ever occupies one slot).

On a failed claim (no session currently available), a slot backs off with a doubling/cap/reset-on-success shape (`min_backoff_seconds` → `max_backoff_seconds`, reset to `min_backoff_seconds` the moment a claim succeeds).

### Why one shared client

**All of a `SessionQueueConsumer`'s `max_concurrent_sessions` slots share the single `DaprMQClient` (and its one gRPC channel) passed into its constructor** - never construct a separate `DaprMQClient` per slot. A gRPC channel multiplexes every RPC, including long-lived streams, over one (or a small number of, if the server's per-connection stream cap is exceeded) underlying HTTP/2 connection - this is what makes running 10s-100s of concurrent `consume_session` streams cheap. Giving each slot its own channel would open that many separate HTTP/2 connections for no benefit - see the "multiplexing many sessions over one stream" discussion in [SESSIONS_IMPLEMENTATION.md](../../../docs/SESSIONS_IMPLEMENTATION.md) for the full reasoning.

## Testing your own code against this SDK

`DaprMQClient`'s `http_client`/`grpc_stub` constructor arguments are DI/test seams - substitute a fake `httpx.AsyncTransport` and a fake gRPC stub to exercise code that depends on `DaprMQClient` without a live server. See `sdks/python/tests/fake_transport.py` and `sdks/python/tests/fake_grpc_stub.py` for the pattern this SDK's own test suite uses.
