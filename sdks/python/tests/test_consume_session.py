from __future__ import annotations

import asyncio
import contextlib

import pytest

from daprmq_client import DaprMQClient, DaprMQError, NoSessionsAvailableError, SessionLostError
from daprmq_client import client as client_module
from daprmq_client.grpc import daprmq_pb2

from .fake_grpc_stub import FakeStreamStreamCall, FakeStub


def make_client(call: FakeStreamStreamCall) -> DaprMQClient:
    return DaprMQClient(http_base_url="http://localhost:5000", grpc_stub=FakeStub(call))


async def test_yields_a_delivered_item_and_ack_writes_an_ack_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)

    call.emit(
        daprmq_pb2.ConsumeSessionResponse(
            session_assigned=daprmq_pb2.SessionAssigned(session_id="order-42", lease_expires_at=1780000200.0)
        )
    )
    call.emit(
        daprmq_pb2.ConsumeSessionResponse(
            delivered=daprmq_pb2.SessionDelivered(lock_id="L1", item_json='{"task":"x"}', priority=0, lock_expires_at=123.0)
        )
    )
    call.emit_end()

    deliveries = []
    async for delivery in client.consume_session("q"):
        deliveries.append(delivery)
        await delivery.ack()

    assert len(deliveries) == 1
    assert deliveries[0].session_id == "order-42"
    assert deliveries[0].lock_id == "L1"
    assert deliveries[0].item == {"task": "x"}

    assert len(call.written) == 2  # Start, then Ack
    assert call.written[0].HasField("start")
    assert call.written[1].ack.lock_id == "L1"


async def test_forwards_session_idle_timeout_seconds_on_the_start_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)
    call.emit_end()

    async for _ in client.consume_session("q", session_idle_timeout_seconds=5):
        pass

    assert call.written[0].start.session_idle_timeout_seconds == 5


async def test_throws_the_mapped_exception_on_an_error_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)

    call.emit(
        daprmq_pb2.ConsumeSessionResponse(
            error=daprmq_pb2.SessionError(error_code="NO_SESSIONS_AVAILABLE", message="none free")
        )
    )
    call.emit_end()

    with pytest.raises(NoSessionsAvailableError):
        async for _ in client.consume_session("q"):
            pass  # drain


async def test_throws_session_lost_error_on_a_session_lost_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)

    call.emit(
        daprmq_pb2.ConsumeSessionResponse(
            session_assigned=daprmq_pb2.SessionAssigned(session_id="order-42", lease_expires_at=1780000200.0)
        )
    )
    call.emit(daprmq_pb2.ConsumeSessionResponse(session_lost=daprmq_pb2.SessionLost(message="lease lost")))
    call.emit_end()

    with pytest.raises(SessionLostError):
        async for _ in client.consume_session("q"):
            pass  # drain


async def test_dead_letter_writes_a_dead_letter_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)

    call.emit(daprmq_pb2.ConsumeSessionResponse(session_assigned=daprmq_pb2.SessionAssigned(session_id="s1", lease_expires_at=1.0)))
    call.emit(
        daprmq_pb2.ConsumeSessionResponse(
            delivered=daprmq_pb2.SessionDelivered(lock_id="L2", item_json="{}", priority=1, lock_expires_at=1.0)
        )
    )
    call.emit_end()

    async for delivery in client.consume_session("q", session_id="s1"):
        await delivery.dead_letter()

    assert call.written[1].dead_letter.lock_id == "L2"


async def test_nack_writes_a_nack_frame() -> None:
    call = FakeStreamStreamCall()
    client = make_client(call)

    call.emit(daprmq_pb2.ConsumeSessionResponse(session_assigned=daprmq_pb2.SessionAssigned(session_id="s1", lease_expires_at=1.0)))
    call.emit(
        daprmq_pb2.ConsumeSessionResponse(
            delivered=daprmq_pb2.SessionDelivered(lock_id="L3", item_json="{}", priority=1, lock_expires_at=1.0)
        )
    )
    call.emit_end()

    async for delivery in client.consume_session("q", session_id="s1"):
        await delivery.nack()

    assert call.written[1].nack.lock_id == "L3"


def delivered(lock_id: str) -> daprmq_pb2.ConsumeSessionResponse:
    return daprmq_pb2.ConsumeSessionResponse(
        delivered=daprmq_pb2.SessionDelivered(lock_id=lock_id, item_json="{}", priority=0, lock_expires_at=1.0)
    )


async def test_breaking_after_acking_half_closes_and_waits_for_the_server_before_cancelling() -> None:
    call = FakeStreamStreamCall(ends_after_half_close=0.1)
    client = make_client(call)
    call.emit(delivered("L1"))

    async with contextlib.aclosing(client.consume_session("q")) as stream:
        async for delivery in stream:
            await delivery.ack()
            break  # consumer shutting down

    assert call.written[1].ack.lock_id == "L1"
    assert call.half_closed
    assert not call.cancelled_before_server_ended


async def test_setting_cancel_after_acking_half_closes_and_waits_for_the_server_before_cancelling() -> None:
    call = FakeStreamStreamCall(ends_after_half_close=0.1)
    client = make_client(call)
    call.emit(delivered("L1"))
    cancel = asyncio.Event()

    async for delivery in client.consume_session("q", cancel=cancel):
        await delivery.ack()
        cancel.set()

    assert call.written[1].ack.lock_id == "L1"
    assert call.half_closed
    assert not call.cancelled_before_server_ended


async def test_cancels_the_call_if_the_server_never_ends_it_after_the_half_close(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(client_module, "SESSION_DRAIN_TIMEOUT_SECONDS", 0.1)
    call = FakeStreamStreamCall()  # never ends on its own
    client = make_client(call)
    cancel = asyncio.Event()
    cancel.set()

    async def consume() -> None:
        async for _ in client.consume_session("q", cancel=cancel):
            pass

    await asyncio.wait_for(consume(), timeout=2)

    assert call.half_closed
    assert call.cancelled


async def test_hands_out_nothing_that_arrives_after_the_half_close() -> None:
    call = FakeStreamStreamCall()
    call._on_half_close = lambda: (call.emit(delivered("L2")), call.emit_end())
    client = make_client(call)
    call.emit(delivered("L1"))
    cancel = asyncio.Event()
    seen: list[str] = []

    async for delivery in client.consume_session("q", cancel=cancel):
        seen.append(delivery.lock_id)
        cancel.set()

    assert seen == ["L1"]


async def test_settling_after_the_half_close_raises_without_writing() -> None:
    call = FakeStreamStreamCall(ends_after_half_close=0)
    client = make_client(call)
    call.emit(delivered("L1"))
    cancel = asyncio.Event()
    kept = []

    async for delivery in client.consume_session("q", cancel=cancel):
        kept.append(delivery)
        cancel.set()
        await asyncio.sleep(0.01)  # let the half-close land

    with pytest.raises(DaprMQError, match="closing"):
        await kept[0].ack()
    assert len(call.written) == 1  # just the Start frame
