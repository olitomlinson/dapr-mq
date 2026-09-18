from __future__ import annotations

import pytest

from daprmq_client import (
    DaprMQClient,
    EnqueueItem,
    InvalidLeaseIdError,
    LockExpiredError,
    LockNotFoundError,
    SessionActorUnavailableError,
    SessionLeaseExpiredError,
    SessionLockedError,
    SessionNotFoundError,
    ValidationError,
)

from .fake_transport import fake_client


def make_client(status_code: int, json_body=None):
    http_client, transport = fake_client(status_code, json_body)
    client = DaprMQClient(http_base_url="http://localhost:5000", http_client=http_client, grpc_stub=object())
    return client, transport


class TestEnqueue:
    async def test_maps_a_successful_response(self) -> None:
        client, transport = make_client(
            200, {"success": True, "message": "Enqueued 1 items", "itemsEnqueued": 1, "itemsDeduplicated": 0}
        )

        result = await client.enqueue("my-queue", [EnqueueItem(item={"task": "send_email"}, priority=0, session_id="order-42")])

        assert result.success is True
        assert result.items_enqueued == 1
        assert transport.last_request.method == "POST"
        assert str(transport.last_request.url) == "http://localhost:5000/queue/my-queue/enqueue"
        body = transport.last_request.content.decode()
        assert '"sessionId":"order-42"' in body
        assert '"priority":0' in body

    async def test_throws_validation_error_on_400(self) -> None:
        client, _ = make_client(400, {"message": "bad item", "success": False})

        with pytest.raises(ValidationError):
            await client.enqueue("q", [EnqueueItem(item={})])


class TestDequeueLocked:
    async def test_sends_headers_and_maps_items(self) -> None:
        client, transport = make_client(
            200, {"items": [{"item": {"task": "x"}, "priority": 0, "lockId": "L1", "lockExpiresAt": 123.0}], "locked": False}
        )

        result = await client.dequeue_locked("q", count=5, ttl_seconds=60, lease_id="lease-1")

        assert result is not None
        assert len(result.items) == 1
        assert result.items[0].lock_id == "L1"
        assert transport.last_request.headers["require-ack"] == "true"
        assert transport.last_request.headers["count"] == "5"
        assert transport.last_request.headers["ttl-seconds"] == "60"
        assert transport.last_request.headers["lease-id"] == "lease-1"

    async def test_returns_none_on_204(self) -> None:
        client, _ = make_client(204)

        assert await client.dequeue_locked("q") is None

    async def test_returns_a_locked_result_on_423(self) -> None:
        client, _ = make_client(423, {"message": "locked", "lockExpiresAt": 999.0})

        result = await client.dequeue_locked("q")

        assert result is not None
        assert result.locked is True
        assert result.items == []

    async def test_throws_session_lease_expired_error_on_410(self) -> None:
        client, _ = make_client(410, {"message": "lease expired", "success": False})

        with pytest.raises(SessionLeaseExpiredError):
            await client.dequeue_locked("q", lease_id="stale")


class TestAcknowledge:
    async def test_sends_the_lease_id_header_on_success(self) -> None:
        client, transport = make_client(200, {"success": True, "message": "ok", "itemsAcknowledged": 1})

        await client.acknowledge("q", "L1", lease_id="lease-1")

        assert transport.last_request.headers["lease-id"] == "lease-1"
        assert '"lockId":"L1"' in transport.last_request.content.decode()

    @pytest.mark.parametrize(
        ("error_code", "expected_error"),
        [
            ("LOCK_NOT_FOUND", LockNotFoundError),
            ("LOCK_EXPIRED", LockExpiredError),
            ("SESSION_LEASE_EXPIRED", SessionLeaseExpiredError),
            ("INVALID_LEASE_ID", InvalidLeaseIdError),
        ],
    )
    async def test_maps_error_code(self, error_code, expected_error) -> None:
        client, _ = make_client(400, {"success": False, "message": "failed", "errorCode": error_code})

        with pytest.raises(expected_error):
            await client.acknowledge("q", "L1")


class TestExtendLock:
    async def test_sends_the_additional_ttl_in_the_body(self) -> None:
        client, transport = make_client(200, {"newExpiresAt": 123, "lockId": "L1"})

        await client.extend_lock("q", "L1", 30)

        assert '"additionalTtlSeconds":30' in transport.last_request.content.decode()

    async def test_throws_lock_expired_error_on_410(self) -> None:
        client, _ = make_client(410, {"message": "expired", "success": False})

        with pytest.raises(LockExpiredError):
            await client.extend_lock("q", "L1", 30)


class TestDeadLetter:
    async def test_resolves_on_success(self) -> None:
        client, _ = make_client(200, {"success": True, "message": "moved", "dlqId": "q-deadletter"})

        await client.dead_letter("q", "L1")

    async def test_maps_error_code_to_a_typed_exception(self) -> None:
        client, _ = make_client(404, {"success": False, "message": "not found", "errorCode": "LOCK_NOT_FOUND"})

        with pytest.raises(LockNotFoundError):
            await client.dead_letter("q", "L1")


class TestSessions:
    async def test_accept_session_maps_a_successful_response(self) -> None:
        client, transport = make_client(200, {"sessionId": "order-42", "leaseId": "lease-1", "leaseExpiresAt": 1780000200.0})

        lease = await client.accept_session("q", session_id="order-42", lease_seconds=30)

        assert lease is not None
        assert lease.session_id == "order-42"
        assert lease.lease_id == "lease-1"
        assert str(transport.last_request.url) == "http://localhost:5000/queue/q/sessions/accept"

    async def test_accept_session_returns_none_on_204(self) -> None:
        client, _ = make_client(204)

        assert await client.accept_session("q") is None

    @pytest.mark.parametrize(
        ("status", "expected_error"),
        [
            (404, SessionNotFoundError),
            (423, SessionLockedError),
            (502, SessionActorUnavailableError),
            (400, ValidationError),
        ],
    )
    async def test_accept_session_maps_status(self, status, expected_error) -> None:
        client, _ = make_client(status, {"message": "failed", "success": False})

        with pytest.raises(expected_error):
            await client.accept_session("q", session_id="order-42")

    async def test_renew_session_lease_succeeds(self) -> None:
        client, transport = make_client(200, {"newExpiresAt": 1780000230.0})

        lease = await client.renew_session_lease("q", "order-42", "lease-1", additional_seconds=30)

        assert lease.lease_expires_at == 1780000230.0
        assert str(transport.last_request.url) == "http://localhost:5000/queue/q/sessions/order-42/renew"

    async def test_renew_session_lease_throws_on_410(self) -> None:
        client, _ = make_client(410, {"message": "expired", "success": False})

        with pytest.raises(SessionLeaseExpiredError):
            await client.renew_session_lease("q", "order-42", "lease-1")

    async def test_release_session_succeeds(self) -> None:
        client, transport = make_client(200, {"success": True})

        await client.release_session("q", "order-42", "lease-1")

        assert str(transport.last_request.url) == "http://localhost:5000/queue/q/sessions/order-42/release"

    async def test_release_session_throws_invalid_lease_id_error_on_400(self) -> None:
        client, _ = make_client(400, {"message": "bad lease", "success": False})

        with pytest.raises(InvalidLeaseIdError):
            await client.release_session("q", "order-42", "wrong")


class TestConstructor:
    def test_requires_grpc_address_unless_grpc_stub_is_supplied(self) -> None:
        with pytest.raises(ValueError, match="grpc_address"):
            DaprMQClient(http_base_url="http://localhost:5000")
