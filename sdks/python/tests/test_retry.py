"""The shared retry contract (sdks/testing/RETRIES_AND_READINESS.md): retry what certainly wasn't
delivered, retry an unknown outcome only for a fully keyed enqueue, within the retry timeout."""

from __future__ import annotations

import asyncio
import json
import re
import time
from collections.abc import Callable

import httpx
import pytest

from daprmq_client import (
    DaprMQClient,
    DaprMQError,
    DaprMQUnavailableError,
    DeliveryUnknownError,
    EnqueueItem,
    RetryOptions,
)

OK = {"success": True, "message": "ok", "itemsEnqueued": 1, "itemsDeduplicated": 0}

FAST = RetryOptions(timeout=5.0, min_attempt_window=0.01, initial_backoff=0.001, max_backoff=0.005)


def not_delivered(request: httpx.Request) -> httpx.Response:
    return httpx.Response(
        503,
        json={"message": "unavailable", "success": False, "errorCode": "UNAVAILABLE"},
        headers={"daprmq-delivery": "not-delivered"},
        request=request,
    )


def unknown(request: httpx.Request) -> httpx.Response:
    return httpx.Response(
        504,
        json={"message": "unknown", "success": False, "errorCode": "DELIVERY_UNKNOWN"},
        headers={"daprmq-delivery": "unknown"},
        request=request,
    )


def ok(body=None) -> Callable[[httpx.Request], httpx.Response]:
    return lambda request: httpx.Response(200, json=body if body is not None else {}, request=request)


class SequenceTransport(httpx.AsyncBaseTransport):
    """Answers each attempt in turn; the last answer repeats."""

    def __init__(self, *answers: Callable[[httpx.Request], httpx.Response]) -> None:
        self.answers = answers
        self.requests: list[httpx.Request] = []

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        await request.aread()
        return self.answers[min(len(self.requests), len(self.answers)) - 1](request)


def make_client(transport: httpx.AsyncBaseTransport, retry: RetryOptions = FAST) -> DaprMQClient:
    return DaprMQClient(
        http_base_url="http://localhost:5000", http_client=httpx.AsyncClient(transport=transport), grpc_stub=object(), retry=retry
    )


async def test_not_delivered_is_retried_until_it_succeeds_for_any_operation() -> None:
    transport = SequenceTransport(not_delivered, not_delivered, ok())

    await make_client(transport).acknowledge("q", "L1")

    assert len(transport.requests) == 3


async def test_every_attempt_sends_the_remaining_time_as_the_deadline() -> None:
    transport = SequenceTransport(ok(OK))

    await make_client(transport).enqueue("q", [EnqueueItem(item={"n": 1})])

    assert 4_000 <= int(transport.requests[0].headers["daprmq-timeout"]) <= 5_000


async def test_not_delivered_until_time_runs_out_raises_unavailable() -> None:
    transport = SequenceTransport(not_delivered)
    client = make_client(transport, RetryOptions(timeout=0.2, min_attempt_window=0.01, initial_backoff=0.001, max_backoff=0.005))

    with pytest.raises(DaprMQUnavailableError) as raised:
        await client.acknowledge("q", "L1")

    assert len(transport.requests) > 1
    assert raised.value.code == "UNAVAILABLE"
    assert raised.value.operation == "acknowledge"
    assert raised.value.queue_id == "q"


async def test_no_attempt_starts_with_less_than_the_minimum_window_left() -> None:
    transport = SequenceTransport(not_delivered)
    client = make_client(transport, RetryOptions(timeout=1.0, min_attempt_window=5.0, initial_backoff=0.001, max_backoff=0.005))

    with pytest.raises(DaprMQUnavailableError):
        await client.acknowledge("q", "L1")

    assert len(transport.requests) == 1


async def test_connection_refused_is_not_delivered_and_retried() -> None:
    attempts = 0

    def answer(request: httpx.Request) -> httpx.Response:
        nonlocal attempts
        attempts += 1
        if attempts < 3:
            raise httpx.ConnectError("refused", request=request)
        return httpx.Response(200, json=OK, request=request)

    await make_client(SequenceTransport(answer)).enqueue("q", [EnqueueItem(item={"n": 1})])

    assert attempts == 3


async def test_unknown_is_not_retried_for_a_dequeue() -> None:
    transport = SequenceTransport(unknown)

    with pytest.raises(DeliveryUnknownError) as raised:
        await make_client(transport).dequeue_locked("q")

    assert len(transport.requests) == 1
    assert raised.value.code == "DELIVERY_UNKNOWN"
    assert raised.value.operation == "dequeue_locked"


async def test_a_broken_connection_after_sending_is_unknown() -> None:
    def answer(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadError("reset", request=request)

    transport = SequenceTransport(answer)

    with pytest.raises(DeliveryUnknownError):
        await make_client(transport).acknowledge("q", "L1")

    assert len(transport.requests) == 1


async def test_unknown_is_not_retried_for_an_enqueue_with_unkeyed_items_and_reports_the_keys() -> None:
    transport = SequenceTransport(unknown)

    with pytest.raises(DeliveryUnknownError) as raised:
        await make_client(transport).enqueue(
            "q", [EnqueueItem(item={"n": 1}, idempotency_key="k1"), EnqueueItem(item={"n": 2})]
        )

    assert len(transport.requests) == 1
    assert raised.value.idempotency_keys == ["k1", None]


async def test_unknown_is_retried_for_a_fully_keyed_enqueue() -> None:
    transport = SequenceTransport(unknown, ok(OK))

    await make_client(transport).enqueue("q", [EnqueueItem(item={"n": 1}, idempotency_key="k1")])

    assert len(transport.requests) == 2


async def test_auto_idempotency_keys_fill_missing_keys_keep_given_ones_and_make_unknown_retryable() -> None:
    transport = SequenceTransport(unknown, ok(OK))
    client = make_client(transport, RetryOptions(timeout=5.0, auto_idempotency_keys=True, min_attempt_window=0.01, initial_backoff=0.001))

    await client.enqueue("q", [EnqueueItem(item={"n": 1}, idempotency_key="mine"), EnqueueItem(item={"n": 2})])

    bodies = [r.content.decode() for r in transport.requests]
    assert len(bodies) == 2
    assert bodies[0] == bodies[1]  # the retry re-sends the same generated key
    keys = [i["idempotencyKey"] for i in json.loads(bodies[0])["items"]]
    assert keys[0] == "mine"
    assert re.fullmatch(r"[0-9a-f]{32}", keys[1])


async def test_a_503_without_the_marker_is_not_a_delivery_failure() -> None:
    transport = SequenceTransport(lambda request: httpx.Response(503, json={"message": "proxy says no"}, request=request))

    with pytest.raises(DaprMQError) as raised:
        await make_client(transport).acknowledge("q", "L1")

    assert not isinstance(raised.value, DaprMQUnavailableError)
    assert len(transport.requests) == 1


async def test_retries_off_send_no_deadline_and_make_one_attempt() -> None:
    transport = SequenceTransport(not_delivered)

    with pytest.raises(DaprMQUnavailableError):
        await make_client(transport, RetryOptions(timeout=0)).acknowledge("q", "L1")

    assert len(transport.requests) == 1
    assert "daprmq-timeout" not in transport.requests[0].headers


async def test_caller_cancellation_stops_retrying_as_cancellation() -> None:
    transport = SequenceTransport(not_delivered)
    client = make_client(transport, RetryOptions(timeout=30.0, min_attempt_window=0.01, initial_backoff=0.02, max_backoff=0.02))

    started = time.monotonic()
    with pytest.raises(asyncio.TimeoutError):
        await asyncio.wait_for(client.acknowledge("q", "L1"), timeout=0.1)

    assert time.monotonic() - started < 2


async def test_enqueue_accepts_a_generator() -> None:
    transport = SequenceTransport(ok(OK))

    await make_client(transport).enqueue("q", (EnqueueItem(item={"n": n}) for n in range(2)))

    assert len(json.loads(transport.requests[0].content)["items"]) == 2
