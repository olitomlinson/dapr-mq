"""Message expiry (proposals/message-expiry.md): a time to live on enqueue, and each delivered
item's expiry_at, expiry_ttl_seconds and original_expiry_ttl_seconds."""

from __future__ import annotations

import asyncio
import json

from daprmq_client import EnqueueItem, QueueConsumerOptions, SessionQueueConsumer, SessionQueueConsumerOptions
from daprmq_client.grpc import daprmq_pb2

from .fake_grpc_stub import FakeStreamStreamCall
from .test_client import make_client
from .test_consume import make_client as make_stream_client
from .test_queue_consumer import FakeClient, FakeStream, make_consumer, wait_until
from .test_session_queue_consumer import FakeClient as SessionFakeClient
from .test_session_queue_consumer import make_delivery, single_item_then_complete

EXPIRY = {"expiry_at": 1800003600.0, "expiry_ttl_seconds": 3600, "original_expiry_ttl_seconds": 600}
NO_EXPIRY = {"expiry_at": None, "expiry_ttl_seconds": None, "original_expiry_ttl_seconds": None}


def expiry_of(obj) -> dict:
    return {k: getattr(obj, k) for k in EXPIRY}


async def test_enqueue_sends_expiry_ttl_seconds_only_when_set() -> None:
    client, transport = make_client(200, {"success": True, "message": "ok", "itemsEnqueued": 2, "itemsDeduplicated": 0})

    await client.enqueue("q", [EnqueueItem(item=1, expiry_ttl_seconds=3600), EnqueueItem(item=2)])

    first, second = json.loads(transport.last_request.content)["items"]
    assert first["expiryTtlSeconds"] == 3600
    assert "expiryTtlSeconds" not in second


async def test_dequeue_locked_maps_each_items_expiry() -> None:
    client, _ = make_client(
        200,
        {
            "items": [
                {"item": 1, "priority": 1, "lockId": "L1", "lockExpiresAt": 1.0,
                 "expiryAt": 1800003600.0, "expiryTtlSeconds": 3600, "originalExpiryTtlSeconds": 600},
                {"item": 2, "priority": 1, "lockId": "L2", "lockExpiresAt": 1.0},
            ],
            "locked": False,
        },
    )

    result = await client.dequeue_locked("q", count=2)

    assert expiry_of(result.items[0]) == EXPIRY
    assert expiry_of(result.items[1]) == NO_EXPIRY


async def test_consume_deliveries_carry_their_expiry() -> None:
    call = FakeStreamStreamCall()
    client = make_stream_client(call)
    call.emit(daprmq_pb2.ConsumeResponse(delivered=daprmq_pb2.ConsumeDelivered(
        lock_id="L1", item_json="{}", priority=1, lock_expires_at=1.0, delivery_count=1,
        expiry_at=1800003600.0, expiry_ttl_seconds=3600, original_expiry_ttl_seconds=600)))
    call.emit(daprmq_pb2.ConsumeResponse(delivered=daprmq_pb2.ConsumeDelivered(
        lock_id="L2", item_json="{}", priority=1, lock_expires_at=1.0, delivery_count=1)))
    call.emit_end()

    deliveries = [d async for d in client.consume("q")]

    assert expiry_of(deliveries[0]) == EXPIRY
    assert expiry_of(deliveries[1]) == NO_EXPIRY


async def test_consume_session_deliveries_carry_their_expiry() -> None:
    call = FakeStreamStreamCall()
    client = make_stream_client(call)
    call.emit(daprmq_pb2.ConsumeSessionResponse(session_assigned=daprmq_pb2.SessionAssigned(session_id="s1", lease_expires_at=1.0)))
    call.emit(daprmq_pb2.ConsumeSessionResponse(delivered=daprmq_pb2.SessionDelivered(
        lock_id="L1", item_json="{}", priority=0, lock_expires_at=1.0,
        expiry_at=1800003600.0, expiry_ttl_seconds=3600, original_expiry_ttl_seconds=600)))
    call.emit(daprmq_pb2.ConsumeSessionResponse(delivered=daprmq_pb2.SessionDelivered(lock_id="L2", item_json="{}")))
    call.emit_end()

    deliveries = [d async for d in client.consume_session("q")]

    assert expiry_of(deliveries[0]) == EXPIRY
    assert expiry_of(deliveries[1]) == NO_EXPIRY


async def test_queue_consumer_context_carries_the_messages_expiry() -> None:
    stream = FakeStream()
    contexts = []

    async def handler(ctx) -> None:
        contexts.append(ctx)

    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(), handler)
    consumer.start()
    stream.deliver("L1", **EXPIRY)
    await wait_until(lambda: len(stream.settled) == 1)
    await consumer.stop()

    assert expiry_of(contexts[0]) == EXPIRY


async def test_session_queue_consumer_context_carries_the_messages_expiry() -> None:
    handle = make_delivery("s1", "L1", **EXPIRY)
    seen: asyncio.Future = asyncio.get_running_loop().create_future()

    async def handler(ctx) -> None:
        if not seen.done():
            seen.set_result(ctx)

    client = SessionFakeClient(lambda: single_item_then_complete(handle.delivery))
    consumer = SessionQueueConsumer(client, "q", SessionQueueConsumerOptions(max_concurrent_sessions=1), handler)
    consumer.start()
    ctx = await asyncio.wait_for(seen, 2)
    await consumer.stop()

    assert expiry_of(ctx) == EXPIRY
