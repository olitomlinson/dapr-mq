from __future__ import annotations

import asyncio
import time
from collections.abc import AsyncIterator
from typing import Any

from daprmq_client import QueueDelivery, StreamClosedError
from daprmq_client.queue_consumer import (
    QueueConsumer,
    QueueConsumerOptions,
    QueueHandlerFailureAction,
    QueueMessageContext,
)


class FakeStream:
    """Stands in for one ``consume`` stream: the test pushes deliveries and settlements are
    recorded. Like the real stream, setting ``cancel`` ends it, and settling afterwards raises."""

    def __init__(self) -> None:
        self._queue: asyncio.Queue[Any] = asyncio.Queue()
        self.settled: list[tuple[str, str]] = []
        self.closed = False

    def deliver(self, lock_id: str, delivery_count: int = 1) -> None:
        async def settle(action: str) -> None:
            if self.closed:
                raise StreamClosedError("closed")
            self.settled.append((action, lock_id))

        self._queue.put_nowait(
            QueueDelivery(
                lock_id=lock_id,
                item={"n": 1},
                priority=1,
                lock_expires_at=0,
                delivery_count=delivery_count,
                ack=lambda: settle("ack"),
                nack=lambda: settle("nack"),
                dead_letter=lambda: settle("dead_letter"),
            )
        )

    def break_(self) -> None:
        """The stream breaks, as when the gateway goes away."""
        self._queue.put_nowait(ConnectionError("stream broke"))

    async def read(self, cancel: asyncio.Event) -> AsyncIterator[QueueDelivery]:
        try:
            while True:
                getter = asyncio.ensure_future(self._queue.get())
                stopper = asyncio.ensure_future(cancel.wait())
                done, _ = await asyncio.wait({getter, stopper}, return_when=asyncio.FIRST_COMPLETED)
                stopper.cancel()
                if getter not in done:
                    getter.cancel()
                    return
                item = getter.result()
                if isinstance(item, Exception):
                    raise item
                yield item
        finally:
            self.closed = True


class FakeClient:
    def __init__(self, *streams: FakeStream) -> None:
        self.streams = streams
        self.opened: list[dict[str, Any]] = []

    def consume(self, queue_id: str, **kwargs: Any) -> AsyncIterator[QueueDelivery]:
        assert queue_id == "q"
        self.opened.append(kwargs)
        stream = self.streams[min(len(self.opened), len(self.streams)) - 1]
        return stream.read(kwargs["cancel"])


async def wait_until(condition, timeout: float = 2.0) -> None:
    start = time.monotonic()
    while not condition():
        assert time.monotonic() - start < timeout, "condition not met"
        await asyncio.sleep(0.005)


def make_consumer(client: FakeClient, options: QueueConsumerOptions, handler) -> tuple[QueueConsumer, list[float]]:
    consumer = QueueConsumer(client, "q", options, handler)
    delays: list[float] = []

    async def fake_delay(seconds: float) -> None:
        delays.append(seconds)
        await asyncio.sleep(0)

    consumer.delay = fake_delay
    return consumer, delays


async def noop(_ctx: QueueMessageContext) -> None:
    pass


async def boom(_ctx: QueueMessageContext) -> None:
    raise RuntimeError("boom")


async def test_opens_the_stream_with_the_defaults() -> None:
    client = FakeClient(FakeStream())
    consumer, _ = make_consumer(client, QueueConsumerOptions(), noop)

    consumer.start()
    await wait_until(lambda: len(client.opened) == 1)
    await consumer.stop()

    opened = client.opened[0]
    assert (opened["prefetch_count"], opened["lock_ttl_seconds"], opened["allow_competing_consumers"]) == (100, 30, True)


async def test_handler_success_acks_and_the_context_carries_the_delivery() -> None:
    stream = FakeStream()
    contexts: list[QueueMessageContext] = []

    async def handler(ctx: QueueMessageContext) -> None:
        contexts.append(ctx)

    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(), handler)
    consumer.start()
    stream.deliver("L1", delivery_count=3)
    await wait_until(lambda: len(stream.settled) == 1)
    await consumer.stop()

    assert stream.settled == [("ack", "L1")]
    (ctx,) = contexts
    assert (ctx.queue_id, ctx.lock_id, ctx.item, ctx.priority, ctx.delivery_count) == ("q", "L1", {"n": 1}, 1, 3)


async def test_handler_error_nacks_by_default() -> None:
    stream = FakeStream()
    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(), boom)
    consumer.start()
    stream.deliver("L1")
    await wait_until(lambda: len(stream.settled) == 1)
    await consumer.stop()

    assert stream.settled == [("nack", "L1")]


async def test_handler_error_dead_letters_when_configured() -> None:
    stream = FakeStream()
    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(on_handler_error=QueueHandlerFailureAction.DEAD_LETTER), boom)
    consumer.start()
    stream.deliver("L1")
    await wait_until(lambda: len(stream.settled) == 1)
    await consumer.stop()

    assert stream.settled == [("dead_letter", "L1")]


async def test_handler_errors_pace_nacks_at_max_retriable_errors_per_sec() -> None:
    stream = FakeStream()
    consumer, delays = make_consumer(FakeClient(stream), QueueConsumerOptions(max_retriable_errors_per_sec=10, strict_order=True), boom)
    consumer.start()
    for lock_id in ("L1", "L2", "L3"):
        stream.deliver(lock_id)
    await wait_until(lambda: len(stream.settled) == 3)
    await consumer.stop()

    # The first nack goes straight away; each later one waits for its own slot, 100 ms after the
    # previous one (the fake delay doesn't pass that time).
    assert len(delays) == 2
    assert 0.05 <= delays[0] <= 0.1
    assert 0.15 <= delays[1] <= 0.2


async def test_max_concurrent_handlers_is_never_exceeded() -> None:
    stream = FakeStream()
    running = 0
    peak = 0

    async def handler(_ctx: QueueMessageContext) -> None:
        nonlocal running, peak
        running += 1
        peak = max(peak, running)
        await asyncio.sleep(0.03)
        running -= 1

    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(max_concurrent_handlers=2), handler)
    consumer.start()
    for i in range(8):
        stream.deliver(f"L{i}")
    await wait_until(lambda: len(stream.settled) == 8)
    await consumer.stop()

    assert peak == 2


async def test_strict_order_opens_a_window_of_one_without_competing_consumers_and_handles_one_at_a_time() -> None:
    stream = FakeStream()
    client = FakeClient(stream)
    running = 0
    peak = 0

    async def handler(_ctx: QueueMessageContext) -> None:
        nonlocal running, peak
        running += 1
        peak = max(peak, running)
        await asyncio.sleep(0.01)
        running -= 1

    consumer, _ = make_consumer(client, QueueConsumerOptions(strict_order=True), handler)
    consumer.start()
    for i in range(4):
        stream.deliver(f"L{i}")
    await wait_until(lambda: len(stream.settled) == 4)
    await consumer.stop()

    assert (client.opened[0]["prefetch_count"], client.opened[0]["allow_competing_consumers"]) == (1, False)
    assert peak == 1
    assert [lock_id for _, lock_id in stream.settled] == ["L0", "L1", "L2", "L3"]


async def test_stop_lets_the_running_handler_ack_before_closing_the_stream_and_starts_no_more() -> None:
    stream = FakeStream()
    started: list[str] = []
    release = asyncio.Event()

    async def handler(ctx: QueueMessageContext) -> None:
        started.append(ctx.lock_id)
        await release.wait()

    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(max_concurrent_handlers=1), handler)
    consumer.start()
    stream.deliver("L1")
    stream.deliver("L2")  # prefetched; waits for the one handler slot
    await wait_until(lambda: len(started) == 1)

    stopping = asyncio.ensure_future(consumer.stop())
    await asyncio.sleep(0.05)
    assert not stream.closed, "the stream must stay open while a handler runs"
    release.set()
    await asyncio.wait_for(stopping, timeout=2)

    assert started == ["L1"]
    assert stream.settled == [("ack", "L1")]
    assert stream.closed


async def test_stop_cancels_handlers_once_the_drain_timeout_runs_out() -> None:
    stream = FakeStream()
    cancelled = asyncio.Event()

    async def handler(_ctx: QueueMessageContext) -> None:
        try:
            await asyncio.sleep(3600)
        finally:
            cancelled.set()

    consumer, _ = make_consumer(FakeClient(stream), QueueConsumerOptions(drain_timeout_seconds=0.1), handler)
    consumer.start()
    stream.deliver("L1")
    await asyncio.sleep(0.05)
    await asyncio.wait_for(consumer.stop(), timeout=2)

    assert cancelled.is_set()
    assert stream.settled == []  # left unsettled: the server returns it when the stream closes


async def test_a_broken_stream_reopens_backing_off_until_a_delivery_resets_it() -> None:
    broken1, broken2, delivering, broken3, last = FakeStream(), FakeStream(), FakeStream(), FakeStream(), FakeStream()
    broken1.break_()
    broken2.break_()
    delivering.deliver("L1")
    client = FakeClient(broken1, broken2, delivering, broken3, last)
    consumer, delays = make_consumer(client, QueueConsumerOptions(min_backoff_seconds=1, max_backoff_seconds=60), noop)

    consumer.start()
    await wait_until(lambda: len(delivering.settled) == 1)
    delivering.break_()
    await wait_until(lambda: len(client.opened) == 4)
    broken3.break_()
    await wait_until(lambda: len(client.opened) == 5)
    await consumer.stop()

    assert delays == [1, 2, 1, 2]
