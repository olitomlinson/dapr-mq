"""E-01..E-03 from sdks/testing/INTEGRATION_TESTS.md - message expiry. The stack sets
DAPRMQ_MIN_EXPIRY_TTL_SECONDS to 1 so an item can expire within a test."""

from __future__ import annotations

import asyncio
import contextlib
import time

from daprmq_client import DaprMQClient, EnqueueItem

# Server default, DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS unset.
DEAD_LETTER_EXPIRY_TTL_SECONDS = 2_419_200


def near(expiry_at: float | None, want: float) -> bool:
    """Allows for the API server stamping the expiry slightly after the test read its clock."""
    return expiry_at is not None and want - 5 <= expiry_at <= want + 30


async def test_e01_expired_item_is_not_delivered(client: DaprMQClient, queue_id: str) -> None:
    await client.enqueue(queue_id, [EnqueueItem({"seq": 1}, expiry_ttl_seconds=1)])

    await asyncio.sleep(2.5)

    assert await client.dequeue_locked(queue_id) is None


async def test_e02_dequeued_and_consumed_items_carry_their_expiry(client: DaprMQClient, queue_id: str) -> None:
    before = time.time()
    await client.enqueue(
        queue_id,
        [EnqueueItem({"seq": 1}, expiry_ttl_seconds=3600), EnqueueItem({"seq": 2}, expiry_ttl_seconds=7200)],
    )

    (locked,) = (await client.dequeue_locked(queue_id)).items
    assert locked.expiry_ttl_seconds == 3600
    assert near(locked.expiry_at, before + 3600)
    assert locked.original_expiry_ttl_seconds is None
    await client.acknowledge(queue_id, locked.lock_id)

    async with contextlib.aclosing(client.consume(queue_id)) as stream:
        async for delivery in stream:
            assert delivery.expiry_ttl_seconds == 7200
            assert near(delivery.expiry_at, before + 7200)
            await delivery.ack()
            break


async def test_e03_dead_lettered_item_carries_the_original_time_to_live(client: DaprMQClient, queue_id: str) -> None:
    await client.enqueue(queue_id, [EnqueueItem({"seq": 1}, expiry_ttl_seconds=3600)])
    (locked,) = (await client.dequeue_locked(queue_id)).items
    await client.dead_letter(queue_id, locked.lock_id)

    (dead,) = (await client.dequeue_locked(f"{queue_id}-deadletter")).items

    assert dead.expiry_ttl_seconds == DEAD_LETTER_EXPIRY_TTL_SECONDS
    assert dead.original_expiry_ttl_seconds == 3600
