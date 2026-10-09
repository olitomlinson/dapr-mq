"""QS-01..QS-04 from sdks/testing/INTEGRATION_TESTS.md - the Consume stream for plain queues.

Leaving the loop inside ``contextlib.aclosing`` half-closes the stream and waits for the server to
finish, so settlements sent before a ``break`` are applied."""

from __future__ import annotations

import asyncio
import contextlib

from daprmq_client import DaprMQClient, EnqueueItem, QueueDelivery


async def enqueue_seqs(client: DaprMQClient, queue_id: str, *seqs: int) -> None:
    await client.enqueue(queue_id, [EnqueueItem({"seq": s}) for s in seqs])


async def test_qs01_consume_delivers_in_order_and_ack_removes_items(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 1, 2, 3)
    delivered: list[QueueDelivery] = []

    async with contextlib.aclosing(client.consume(queue_id, prefetch_count=2, allow_competing_consumers=True)) as stream:
        async for delivery in stream:
            delivered.append(delivery)
            await delivery.ack()
            if len(delivered) == 3:
                break

    assert [d.item["seq"] for d in delivered] == [1, 2, 3]
    assert all(d.delivery_count == 1 for d in delivered)
    assert await client.dequeue_locked(queue_id) is None


async def test_qs02_consume_nack_redelivers_with_the_next_delivery_count(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 1)
    delivered: list[QueueDelivery] = []

    async with contextlib.aclosing(client.consume(queue_id)) as stream:
        async for delivery in stream:
            delivered.append(delivery)
            if len(delivered) == 1:
                await delivery.nack()
                continue
            await delivery.ack()
            break

    assert [d.item["seq"] for d in delivered] == [1, 1]
    assert [d.delivery_count for d in delivered] == [1, 2]


async def test_qs03_consume_keeps_a_delivered_item_locked_past_its_ttl(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 1)
    settle_failures: list[str] = []

    async with contextlib.aclosing(
        client.consume(
            queue_id, lock_ttl_seconds=2, allow_competing_consumers=True,
            on_settle_failed=lambda lock_id, _err: settle_failures.append(lock_id),
        )
    ) as stream:
        async for delivery in stream:
            await asyncio.sleep(6)
            assert await client.dequeue_locked(queue_id, allow_competing_consumers=True) is None
            await delivery.ack()
            break

    assert settle_failures == []


async def test_qs04_closing_the_stream_returns_unsettled_items_straight_away(client: DaprMQClient, queue_id: str) -> None:
    await enqueue_seqs(client, queue_id, 1)

    async with contextlib.aclosing(client.consume(queue_id, lock_ttl_seconds=300)) as stream:
        async for _ in stream:
            break

    back = await client.dequeue_locked(queue_id)
    assert back is not None and [i.item["seq"] for i in back.items] == [1]
