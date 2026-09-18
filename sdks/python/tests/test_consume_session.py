from __future__ import annotations

import pytest

from daprmq_client import DaprMQClient, NoSessionsAvailableError, SessionLostError
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
