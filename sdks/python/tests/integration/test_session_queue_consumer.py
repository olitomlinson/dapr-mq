from __future__ import annotations

import asyncio
import time

from daprmq_client import DaprMQClient, EnqueueItem, SessionMessageContext, SessionQueueConsumer, SessionQueueConsumerOptions

ITEMS_PER_SESSION = 5
SLOW_HANDLER_SECONDS = 0.3


async def test_k02_multi_session_preserves_per_session_order_and_isolates_throughput(
    client: DaprMQClient, queue_id: str
) -> None:
    for seq in range(1, ITEMS_PER_SESSION + 1):
        for session_id in ("fast", "slow"):
            await client.enqueue(
                queue_id, [EnqueueItem({"sessionId": session_id, "seq": seq}, priority=1, session_id=session_id)]
            )

    observed: dict[str, list[int]] = {"fast": [], "slow": []}
    done = {"fast": asyncio.Event(), "slow": asyncio.Event()}
    started = time.monotonic()
    fast_completed_at: list[float] = []

    async def handler(ctx: SessionMessageContext) -> None:
        if ctx.session_id == "slow":
            await asyncio.sleep(SLOW_HANDLER_SECONDS)

        observed[ctx.session_id].append(ctx.item["seq"])
        if len(observed[ctx.session_id]) == ITEMS_PER_SESSION:
            if ctx.session_id == "fast":
                fast_completed_at.append(time.monotonic() - started)
            done[ctx.session_id].set()

    options = SessionQueueConsumerOptions(
        max_concurrent_sessions=2,
        lease_seconds=30,
        prefetch_count=10,
        min_backoff_seconds=1,
        max_backoff_seconds=2,
    )

    async with SessionQueueConsumer(client, queue_id, options, handler):
        await asyncio.wait_for(asyncio.gather(done["fast"].wait(), done["slow"].wait()), timeout=30)

    expected = list(range(1, ITEMS_PER_SESSION + 1))
    assert observed["fast"] == expected
    assert observed["slow"] == expected

    # "slow" needs >= ITEMS_PER_SESSION * 0.3s (sequential within its own stream); "fast" must
    # finish well inside that, proving the sessions run on independent streams/slots.
    slow_floor = ITEMS_PER_SESSION * SLOW_HANDLER_SECONDS
    assert fast_completed_at, "fast session never completed"
    assert fast_completed_at[0] < slow_floor, (
        f"fast session took {fast_completed_at[0]:.2f}s, expected well under slow's {slow_floor:.2f}s floor"
    )
