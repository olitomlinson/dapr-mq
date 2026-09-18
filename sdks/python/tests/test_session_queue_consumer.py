from __future__ import annotations

import asyncio
import time
from collections.abc import AsyncIterator
from dataclasses import dataclass

import pytest

from daprmq_client import NoSessionsAvailableError, SessionDelivery
from daprmq_client.session_queue_consumer import (
    SessionHandlerFailureAction,
    SessionMessageContext,
    SessionQueueConsumer,
    SessionQueueConsumerOptions,
)


@dataclass
class DeliveryHandle:
    delivery: SessionDelivery
    acked: list[bool]
    dead_lettered: list[bool]


def make_delivery(session_id: str, lock_id: str) -> DeliveryHandle:
    acked = [False]
    dead_lettered = [False]

    async def ack() -> None:
        acked[0] = True

    async def dead_letter() -> None:
        dead_lettered[0] = True

    delivery = SessionDelivery(
        session_id=session_id, lock_id=lock_id, item={}, priority=0, lock_expires_at=0, ack=ack, dead_letter=dead_letter
    )
    return DeliveryHandle(delivery=delivery, acked=acked, dead_lettered=dead_lettered)


async def macrotask_yield() -> None:
    await asyncio.sleep(0)


async def single_item_then_complete(delivery: SessionDelivery, **_kwargs: object) -> AsyncIterator[SessionDelivery]:
    await macrotask_yield()
    yield delivery


async def throwing_sequence(err: Exception, **_kwargs: object) -> AsyncIterator[SessionDelivery]:
    await macrotask_yield()
    raise err
    yield  # pragma: no cover - makes this an async generator


async def wait_until(condition, timeout: float = 2.0) -> None:
    start = time.monotonic()
    while not condition() and time.monotonic() - start < timeout:
        await asyncio.sleep(0.01)


class FakeClient:
    def __init__(self, factory) -> None:
        self.factory = factory
        self.call_count = 0

    def consume_session(self, queue_id: str, **kwargs: object) -> AsyncIterator[SessionDelivery]:
        self.call_count += 1
        return self.factory()


async def test_claims_delivers_handles_and_acks_on_the_happy_path() -> None:
    handle = make_delivery("s1", "L1")
    handler_called = asyncio.Event()

    client = FakeClient(lambda: single_item_then_complete(handle.delivery))

    async def handler(ctx: SessionMessageContext) -> None:
        assert ctx.session_id == "s1"
        assert ctx.lock_id == "L1"
        handler_called.set()

    consumer = SessionQueueConsumer(client, "q", SessionQueueConsumerOptions(max_concurrent_sessions=1), handler)

    consumer.start()
    await handler_called.wait()
    await wait_until(lambda: handle.acked[0])
    await consumer.stop()

    assert handle.acked[0] is True
    assert handle.dead_lettered[0] is False


async def test_backs_off_doubling_on_repeated_no_sessions_available_then_resets() -> None:
    delays: list[float] = []
    call_count = [0]

    def factory():
        call_count[0] += 1
        if call_count[0] <= 3:
            return throwing_sequence(NoSessionsAvailableError("none"))
        return single_item_then_complete(make_delivery("s1", "L1").delivery)

    client = FakeClient(factory)

    async def handler(_ctx: SessionMessageContext) -> None:
        pass

    consumer = SessionQueueConsumer(
        client, "q", SessionQueueConsumerOptions(max_concurrent_sessions=1, min_backoff_seconds=1, max_backoff_seconds=60), handler
    )

    async def fake_delay(seconds: float) -> None:
        delays.append(seconds)
        await asyncio.sleep(0)

    consumer.delay = fake_delay

    consumer.start()
    await wait_until(lambda: len(delays) >= 3)
    await consumer.stop()

    assert len(delays) >= 3
    assert delays[0] == 1
    assert delays[1] == 2
    assert delays[2] == 4


async def test_caps_backoff_at_max_backoff_seconds() -> None:
    delays: list[float] = []

    client = FakeClient(lambda: throwing_sequence(NoSessionsAvailableError("none")))

    async def handler(_ctx: SessionMessageContext) -> None:
        pass

    consumer = SessionQueueConsumer(
        client, "q", SessionQueueConsumerOptions(max_concurrent_sessions=1, min_backoff_seconds=1, max_backoff_seconds=4), handler
    )

    async def fake_delay(seconds: float) -> None:
        delays.append(seconds)
        await asyncio.sleep(0)

    consumer.delay = fake_delay

    consumer.start()
    await wait_until(lambda: len(delays) >= 5)
    await consumer.stop()

    assert len(delays) >= 5
    assert delays[0] == 1
    assert delays[1] == 2
    assert delays[2] == 4
    assert delays[3] == 4  # capped
    assert delays[4] == 4


async def test_dead_letters_the_message_by_default_when_the_handler_throws() -> None:
    handle = make_delivery("s1", "L1")
    dead_letter_triggered = asyncio.Event()

    client = FakeClient(lambda: single_item_then_complete(handle.delivery))

    async def handler(_ctx: SessionMessageContext) -> None:
        dead_letter_triggered.set()
        raise RuntimeError("handler blew up")

    consumer = SessionQueueConsumer(
        client,
        "q",
        SessionQueueConsumerOptions(max_concurrent_sessions=1, on_handler_exception=SessionHandlerFailureAction.DEAD_LETTER_MESSAGE),
        handler,
    )

    consumer.start()
    await dead_letter_triggered.wait()
    await wait_until(lambda: handle.dead_lettered[0])
    await consumer.stop()

    assert handle.dead_lettered[0] is True
    assert handle.acked[0] is False


async def test_abandon_session_does_not_dead_letter_and_keeps_the_slot_alive() -> None:
    handle = make_delivery("s1", "L1")
    client = FakeClient(lambda: single_item_then_complete(handle.delivery))

    handler_ran = asyncio.Event()

    async def handler(_ctx: SessionMessageContext) -> None:
        handler_ran.set()
        raise RuntimeError("handler blew up")

    consumer = SessionQueueConsumer(
        client, "q", SessionQueueConsumerOptions(max_concurrent_sessions=1, on_handler_exception=SessionHandlerFailureAction.ABANDON_SESSION), handler
    )

    consumer.start()
    await handler_ran.wait()
    await wait_until(lambda: client.call_count >= 2)
    await consumer.stop()

    assert handle.dead_lettered[0] is False
    assert handle.acked[0] is False
    assert client.call_count >= 2


async def test_stop_drains_gracefully_without_throwing() -> None:
    client = FakeClient(lambda: throwing_sequence(NoSessionsAvailableError("none")))

    async def handler(_ctx: SessionMessageContext) -> None:
        pass

    consumer = SessionQueueConsumer(client, "q", SessionQueueConsumerOptions(max_concurrent_sessions=2), handler)
    consumer.delay = lambda _seconds: macrotask_yield()

    consumer.start()
    await asyncio.sleep(0.02)
    await consumer.stop()  # must not raise


def test_raises_when_target_session_id_set_without_max_concurrent_sessions_1() -> None:
    client = FakeClient(lambda: single_item_then_complete(make_delivery("s1", "L1").delivery))

    async def handler(_ctx: SessionMessageContext) -> None:
        pass

    with pytest.raises(ValueError):
        SessionQueueConsumer(
            client, "q", SessionQueueConsumerOptions(target_session_id="s1", max_concurrent_sessions=2), handler
        )
