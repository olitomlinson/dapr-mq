from __future__ import annotations

import asyncio
import contextlib

import pytest

from daprmq_client import DaprMQClient, DaprMQError, LockNotFoundError, StreamClosedError, ValidationError
from daprmq_client import client as client_module
from daprmq_client.grpc import daprmq_pb2

from .fake_grpc_stub import FakeStreamStreamCall, FakeStub


def make_client(call: FakeStreamStreamCall) -> DaprMQClient:
    return DaprMQClient(http_base_url="http://localhost:5000", grpc_stub=FakeStub(call))


def delivered(lock_id: str, delivery_count: int = 1) -> daprmq_pb2.ConsumeResponse:
    return daprmq_pb2.ConsumeResponse(
        delivered=daprmq_pb2.ConsumeDelivered(
            lock_id=lock_id, item_json='{"task":"x"}', priority=1, lock_expires_at=123.0, delivery_count=delivery_count
        )
    )


async def test_yields_a_delivered_item_and_ack_writes_an_ack_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)
    call.emit(delivered("L1", delivery_count=2))
    call.emit_end()

    deliveries = []
    async for delivery in client.consume("q"):
        deliveries.append(delivery)
        await delivery.ack()

    (d,) = deliveries
    assert (d.lock_id, d.item, d.priority, d.lock_expires_at, d.delivery_count) == ("L1", {"task": "x"}, 1, 123.0, 2)
    assert len(call.written) == 2  # Start, then Ack
    assert call.written[1].ack.lock_id == "L1"


async def test_the_start_frame_uses_the_defaults() -> None:
    call = FakeStreamStreamCall()
    call.emit_end()

    async for _ in make_client(call).consume("orders"):
        pass

    start = call.written[0].start
    assert (start.queue_id, start.prefetch_count, start.lock_ttl_seconds, start.allow_competing_consumers) == ("orders", 1, 30, False)


async def test_the_start_frame_carries_the_options_with_the_lock_ttl_rounded_up() -> None:
    call = FakeStreamStreamCall()
    call.emit_end()

    async for _ in make_client(call).consume("orders", prefetch_count=50, lock_ttl_seconds=10.2, allow_competing_consumers=True):
        pass

    start = call.written[0].start
    assert (start.prefetch_count, start.lock_ttl_seconds, start.allow_competing_consumers) == (50, 11, True)


async def test_nack_and_dead_letter_write_their_frames() -> None:
    call = FakeStreamStreamCall()
    call.emit(delivered("L1"))
    call.emit(delivered("L2"))
    call.emit_end()

    async for delivery in make_client(call).consume("q", prefetch_count=2):
        if delivery.lock_id == "L1":
            await delivery.nack()
        else:
            await delivery.dead_letter()

    assert call.written[1].nack.lock_id == "L1"
    assert call.written[2].dead_letter.lock_id == "L2"


async def test_a_settle_failed_frame_goes_to_the_callback_and_the_stream_carries_on() -> None:
    call = FakeStreamStreamCall()
    call.emit(daprmq_pb2.ConsumeResponse(settle_failed=daprmq_pb2.ConsumeSettleFailed(lock_id="L0", error_code="LOCK_NOT_FOUND", message="gone")))
    call.emit(delivered("L1"))
    call.emit_end()
    failures: list[tuple[str, DaprMQError]] = []

    seen = [d.lock_id async for d in make_client(call).consume("q", on_settle_failed=lambda lock_id, err: failures.append((lock_id, err)))]

    assert seen == ["L1"]
    ((lock_id, err),) = failures
    assert lock_id == "L0"
    assert isinstance(err, LockNotFoundError)


async def test_an_error_frame_raises_the_mapped_error() -> None:
    call = FakeStreamStreamCall()
    call.emit(daprmq_pb2.ConsumeResponse(error=daprmq_pb2.ConsumeError(error_code="INVALID_ARGUMENT", message="bad start")))
    call.emit_end()

    with pytest.raises(ValidationError, match="bad start"):
        async for _ in make_client(call).consume("q"):
            pass


async def test_settling_after_the_stream_ended_raises_stream_closed() -> None:
    call = FakeStreamStreamCall()
    call.emit(delivered("L1"))
    call.emit_end()

    kept = [d async for d in make_client(call).consume("q")]

    with pytest.raises(StreamClosedError):
        await kept[0].ack()


async def test_breaking_after_acking_half_closes_and_waits_for_the_server_before_cancelling() -> None:
    call = FakeStreamStreamCall(ends_after_half_close=0.1)
    call.emit(delivered("L1"))

    async with contextlib.aclosing(make_client(call).consume("q")) as stream:
        async for delivery in stream:
            await delivery.ack()
            break

    assert call.written[1].ack.lock_id == "L1"
    assert call.half_closed
    assert not call.cancelled_before_server_ended


async def test_setting_cancel_hands_out_nothing_more_and_waits_for_the_server() -> None:
    call = FakeStreamStreamCall()
    call._on_half_close = lambda: (call.emit(delivered("L2")), call.emit_end())
    call.emit(delivered("L1"))
    cancel = asyncio.Event()
    seen: list[str] = []

    async for delivery in make_client(call).consume("q", cancel=cancel):
        seen.append(delivery.lock_id)
        cancel.set()

    assert seen == ["L1"]
    assert not call.cancelled_before_server_ended


async def test_cancels_the_call_if_the_server_never_ends_it_after_the_half_close(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(client_module, "SESSION_DRAIN_TIMEOUT_SECONDS", 0.1)
    call = FakeStreamStreamCall()  # never ends on its own
    cancel = asyncio.Event()
    cancel.set()

    async def consume() -> None:
        async for _ in make_client(call).consume("q", cancel=cancel):
            pass

    await asyncio.wait_for(consume(), timeout=2)

    assert call.half_closed
    assert call.cancelled
