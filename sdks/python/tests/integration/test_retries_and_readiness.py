"""R-01, R-04 and R-05 from sdks/testing/RETRIES_AND_READINESS.md against a real server.
(R-02/R-03 need a split gateway/worker stack, which only the .NET fixture builds today.)"""

from __future__ import annotations

import asyncio
import time
import uuid

import pytest

from daprmq_client import DaprMQClient, DaprMQUnavailableError, EnqueueItem, RetryOptions


async def test_r01_wait_for_ready_returns_and_an_enqueue_then_succeeds(client: DaprMQClient, queue_id: str) -> None:
    await asyncio.wait_for(client.wait_for_ready(), timeout=30)

    result = await client.enqueue(queue_id, [EnqueueItem(item={"seq": 1})])
    assert result.items_enqueued == 1


async def test_r04_server_unreachable_raises_unavailable_not_a_hang(queue_id: str) -> None:
    # Port 1 on loopback: nothing listens there, so connections are refused immediately.
    async with DaprMQClient(
        http_base_url="http://127.0.0.1:1", grpc_address="127.0.0.1:1", retry=RetryOptions(timeout=2.0)
    ) as unreachable:
        started = time.monotonic()
        with pytest.raises(DaprMQUnavailableError) as raised:
            await unreachable.enqueue(queue_id, [EnqueueItem(item={"seq": 1})])

    assert raised.value.operation == "enqueue"
    assert time.monotonic() - started < 10


async def test_r05_auto_idempotency_keys_fill_missing_keys_and_keep_given_ones(daprmq_server, queue_id: str) -> None:
    async with DaprMQClient(
        http_base_url=daprmq_server.http_url,
        grpc_address=daprmq_server.grpc_address,
        retry=RetryOptions(auto_idempotency_keys=True),
    ) as client:
        items = [EnqueueItem(item={"seq": 1}, idempotency_key=f"mine-{uuid.uuid4().hex}"), EnqueueItem(item={"seq": 2})]

        await client.enqueue(queue_id, items)
        second = await client.enqueue(queue_id, items)

    # The caller's key is kept (the repeat is de-duplicated); the unkeyed item gets a fresh key per call.
    assert second.items_deduplicated == 1
    assert second.items_enqueued == 1
