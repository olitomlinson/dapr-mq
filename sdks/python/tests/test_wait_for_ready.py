"""wait_for_ready (sdks/testing/RETRIES_AND_READINESS.md): grpc.health.v1 Watch until SERVING."""

from __future__ import annotations

import asyncio

import grpc
import pytest
from grpc_health.v1 import health_pb2

from daprmq_client import DaprMQClient

SERVING = health_pb2.HealthCheckResponse.SERVING
NOT_SERVING = health_pb2.HealthCheckResponse.NOT_SERVING


class FakeRpcError(grpc.aio.AioRpcError):
    def __init__(self, code: grpc.StatusCode) -> None:
        super().__init__(code, grpc.aio.Metadata(), grpc.aio.Metadata(), details="")


class Hang(list):
    """A Watch stream that sends these statuses, then stays open without reaching SERVING."""


class FakeHealthStub:
    """Each Watch call plays the next scripted stream (the last one repeats): a list of statuses
    that then ends, a Hang that stays open, or an error code."""

    def __init__(self, *calls) -> None:
        self.calls = list(calls)
        self.requests: list[health_pb2.HealthCheckRequest] = []

    def Watch(self, request):  # noqa: N802 - mirrors the generated stub
        self.requests.append(request)
        script = self.calls.pop(0) if len(self.calls) > 1 else self.calls[0]

        async def stream():
            if isinstance(script, grpc.StatusCode):
                raise FakeRpcError(script)
            for status in script:
                yield health_pb2.HealthCheckResponse(status=status)
            if isinstance(script, Hang):
                await asyncio.sleep(3600)

        return stream()


def make_client(stub: FakeHealthStub) -> DaprMQClient:
    return DaprMQClient(http_base_url="http://localhost:5000", grpc_stub=object(), health_stub=stub)


async def test_not_serving_then_serving_returns_and_watches_the_operations_service() -> None:
    stub = FakeHealthStub([NOT_SERVING, SERVING])

    await make_client(stub).wait_for_ready()

    assert [r.service for r in stub.requests] == ["daprmq.DaprMQ.operations"]


async def test_another_service_can_be_watched() -> None:
    stub = FakeHealthStub([SERVING])

    await make_client(stub).wait_for_ready("daprmq.DaprMQ")

    assert stub.requests[0].service == "daprmq.DaprMQ"


async def test_unavailable_then_serving_reconnects() -> None:
    stub = FakeHealthStub(grpc.StatusCode.UNAVAILABLE, [SERVING])

    await make_client(stub).wait_for_ready()

    assert len(stub.requests) == 2


async def test_stream_ending_before_serving_reconnects() -> None:
    stub = FakeHealthStub([NOT_SERVING], [SERVING])

    await make_client(stub).wait_for_ready()

    assert len(stub.requests) == 2


async def test_unimplemented_raises_not_implemented() -> None:
    with pytest.raises(NotImplementedError):
        await make_client(FakeHealthStub(grpc.StatusCode.UNIMPLEMENTED)).wait_for_ready()


async def test_never_serving_is_bounded_by_the_caller() -> None:
    with pytest.raises(asyncio.TimeoutError):
        await asyncio.wait_for(make_client(FakeHealthStub(Hang([NOT_SERVING]))).wait_for_ready(), timeout=0.1)
