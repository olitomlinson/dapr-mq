from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncIterator, Iterable
from types import TracebackType
from typing import Any
from urllib.parse import quote

import grpc
import grpc.aio
import httpx

from .errors import (
    ActorNotFoundError,
    DaprMQError,
    InvalidLeaseIdError,
    LockExpiredError,
    LockNotFoundError,
    NoSessionsAvailableError,
    SessionActorUnavailableError,
    SessionLeaseExpiredError,
    SessionLockedError,
    SessionLostError,
    SessionNotFoundError,
    ValidationError,
)
from .grpc import daprmq_pb2, daprmq_pb2_grpc
from .types import DequeueLockedItem, DequeueLockedResult, EnqueueItem, EnqueueResult, SessionDelivery, SessionLease


class DaprMQClient:
    """REST-backed for every operation except :meth:`consume_session`, which is the one method
    built on the ``ConsumeSession`` gRPC streaming RPC - everything else (enqueue, dequeue_locked,
    acknowledge, extend_lock, dead_letter, accept_session, renew_session_lease,
    release_session) is a plain HTTP call under the hood.
    """

    def __init__(
        self,
        *,
        http_base_url: str,
        grpc_address: str | None = None,
        grpc_credentials: grpc.ChannelCredentials | None = None,
        http_client: httpx.AsyncClient | None = None,
        grpc_stub: Any | None = None,
    ) -> None:
        self._http_base_url = http_base_url.rstrip("/")
        self._http_client = http_client or httpx.AsyncClient()
        self._owns_http_client = http_client is None

        if grpc_stub is not None:
            self._grpc_stub = grpc_stub
            self._grpc_channel: grpc.aio.Channel | None = None
            self._owns_grpc_channel = False
        else:
            if not grpc_address:
                raise ValueError("grpc_address is required unless grpc_stub is supplied.")
            self._grpc_channel = (
                grpc.aio.secure_channel(grpc_address, grpc_credentials)
                if grpc_credentials is not None
                else grpc.aio.insecure_channel(grpc_address)
            )
            self._grpc_stub = daprmq_pb2_grpc.DaprMQStub(self._grpc_channel)
            self._owns_grpc_channel = True

    async def enqueue(self, queue_id: str, items: Iterable[EnqueueItem]) -> EnqueueResult:
        body = {
            "items": [
                {
                    "item": i.item,
                    "priority": i.priority,
                    "idempotencyKey": i.idempotency_key,
                    "sessionId": i.session_id,
                }
                for i in items
            ]
        }

        response = await self._http_client.post(self._path(queue_id, "enqueue"), json=body)
        if not response.is_success:
            raise await self._map_generic_error(response)

        result = self._require_parsed(response)
        return EnqueueResult(
            success=result["success"],
            message=result["message"],
            items_enqueued=result["itemsEnqueued"],
            items_deduplicated=result.get("itemsDeduplicated", 0),
        )

    async def dequeue_locked(
        self,
        queue_id: str,
        *,
        count: int = 1,
        ttl_seconds: int = 30,
        lease_id: str | None = None,
    ) -> DequeueLockedResult | None:
        headers = {"require-ack": "true", "count": str(count), "ttl-seconds": str(ttl_seconds)}
        if lease_id is not None:
            headers["lease-id"] = lease_id

        response = await self._http_client.post(self._path(queue_id, "dequeue"), headers=headers)

        if response.status_code == 204:
            return None

        if response.status_code == 423:
            locked = self._try_parse_json(response)
            return DequeueLockedResult(items=[], locked=True, message=(locked or {}).get("message"))

        if not response.is_success:
            message = self._error_message_from(response)
            # Dequeue's guard rejection carries no wire error code - 410 here is unambiguously the
            # session-lease guard (plain lock expiry doesn't apply to Dequeue), 400 covers both a
            # bad/missing lease-id and ordinary request validation (e.g. bad count).
            raise SessionLeaseExpiredError(message) if response.status_code == 410 else ValidationError(message)

        result = self._require_parsed(response)
        items = [
            DequeueLockedItem(item=i["item"], priority=i["priority"], lock_id=i["lockId"], lock_expires_at=i["lockExpiresAt"])
            for i in result["items"]
        ]
        return DequeueLockedResult(items=items, locked=result["locked"], message=result.get("message"))

    async def acknowledge(self, queue_id: str, lock_id: str, *, lease_id: str | None = None) -> None:
        response = await self._post_json(self._path(queue_id, "acknowledge"), {"lockId": lock_id}, lease_id)
        if response.is_success:
            return

        body = self._try_parse_json(response)
        error_code = (body or {}).get("errorCode")
        message = (body or {}).get("message") or self._error_message_from(response)
        raise self._map_lock_error(error_code, message)

    async def extend_lock(
        self, queue_id: str, lock_id: str, additional_ttl_seconds: int, *, lease_id: str | None = None
    ) -> None:
        response = await self._post_json(
            self._path(queue_id, "extend-lock"),
            {"lockId": lock_id, "additionalTtlSeconds": additional_ttl_seconds},
            lease_id,
        )
        if response.is_success:
            return

        message = self._error_message_from(response)
        # ExtendLock's error body carries no wire error code (unlike Acknowledge/DeadLetter), so
        # this is a best-effort status-based mapping only.
        if response.status_code == 410:
            raise LockExpiredError(message)
        if response.status_code == 404:
            raise LockNotFoundError(message)
        raise ValidationError(message)

    async def dead_letter(self, queue_id: str, lock_id: str, *, lease_id: str | None = None) -> None:
        response = await self._post_json(self._path(queue_id, "deadletter"), {"lockId": lock_id}, lease_id)
        if response.is_success:
            return

        body = self._try_parse_json(response)
        error_code = (body or {}).get("errorCode")
        message = (body or {}).get("message") or self._error_message_from(response)
        raise self._map_lock_error(error_code, message)

    async def accept_session(
        self, queue_id: str, *, session_id: str | None = None, lease_seconds: int = 30
    ) -> SessionLease | None:
        response = await self._post_json(
            self._path(queue_id, "sessions/accept"), {"sessionId": session_id, "leaseSeconds": lease_seconds}, None
        )

        if response.status_code == 204:
            return None

        if not response.is_success:
            message = self._error_message_from(response)
            if response.status_code == 404:
                raise SessionNotFoundError(message)
            if response.status_code == 423:
                raise SessionLockedError(message)
            if response.status_code == 502:
                raise SessionActorUnavailableError(message)
            raise ValidationError(message)

        result = self._require_parsed(response)
        return SessionLease(session_id=result["sessionId"], lease_id=result["leaseId"], lease_expires_at=result["leaseExpiresAt"])

    async def renew_session_lease(
        self, queue_id: str, session_id: str, lease_id: str, *, additional_seconds: int = 30
    ) -> SessionLease:
        response = await self._post_json(
            self._path(queue_id, f"sessions/{quote(session_id, safe='')}/renew"),
            {"leaseId": lease_id, "additionalSeconds": additional_seconds},
            None,
        )

        if not response.is_success:
            message = self._error_message_from(response)
            raise SessionLeaseExpiredError(message) if response.status_code == 410 else InvalidLeaseIdError(message)

        result = self._require_parsed(response)
        return SessionLease(session_id=session_id, lease_id=lease_id, lease_expires_at=result["newExpiresAt"])

    async def release_session(self, queue_id: str, session_id: str, lease_id: str) -> None:
        response = await self._post_json(
            self._path(queue_id, f"sessions/{quote(session_id, safe='')}/release"), {"leaseId": lease_id}, None
        )
        if not response.is_success:
            raise InvalidLeaseIdError(self._error_message_from(response))

    async def consume_session(
        self,
        queue_id: str,
        *,
        session_id: str | None = None,
        lease_seconds: int = 30,
        prefetch_count: int = 10,
        cancel: asyncio.Event | None = None,
    ) -> AsyncIterator[SessionDelivery]:
        """Managed consume loop for exactly one session: claims a session (any-available or
        targeted), streams delivered items back, and lets the caller ack/dead_letter each one. No
        lease_id is exposed here (unlike the unary session API) - the server tracks the lease
        internally and the gRPC call itself renews it for as long as the stream stays open.

        Pass `cancel` (an `asyncio.Event`) to interrupt the stream early - setting it cancels the
        underlying gRPC call, unblocking the stream read.
        """
        call = self._grpc_stub.ConsumeSession()

        watcher: asyncio.Task[None] | None = None
        if cancel is not None:

            async def _watch() -> None:
                await cancel.wait()
                call.cancel()

            watcher = asyncio.ensure_future(_watch())

        try:
            start = daprmq_pb2.ConsumeSessionStart(queue_id=queue_id, lease_seconds=lease_seconds, prefetch_count=prefetch_count)
            if session_id is not None:
                start.session_id = session_id
            await call.write(daprmq_pb2.ConsumeSessionRequest(start=start))

            assigned_session_id = session_id or ""

            async for response in call:
                payload = response.WhichOneof("payload")

                if payload == "session_assigned":
                    assigned_session_id = response.session_assigned.session_id

                elif payload == "delivered":
                    delivered = response.delivered
                    lock_id = delivered.lock_id

                    async def ack(lock_id: str = lock_id) -> None:
                        await call.write(daprmq_pb2.ConsumeSessionRequest(ack=daprmq_pb2.ConsumeSessionAck(lock_id=lock_id)))

                    async def dead_letter(lock_id: str = lock_id) -> None:
                        await call.write(
                            daprmq_pb2.ConsumeSessionRequest(dead_letter=daprmq_pb2.ConsumeSessionDeadLetter(lock_id=lock_id))
                        )

                    yield SessionDelivery(
                        session_id=assigned_session_id,
                        lock_id=lock_id,
                        item=json.loads(delivered.item_json),
                        priority=delivered.priority,
                        lock_expires_at=delivered.lock_expires_at,
                        ack=ack,
                        dead_letter=dead_letter,
                    )

                elif payload == "error":
                    raise self._map_session_error(response.error.error_code, response.error.message)

                elif payload == "session_lost":
                    raise SessionLostError(response.session_lost.message)
        finally:
            if watcher is not None:
                watcher.cancel()
            try:
                await call.done_writing()
            except Exception:
                pass  # best-effort - the stream may already be broken/cancelled

    async def aclose(self) -> None:
        if self._owns_http_client:
            await self._http_client.aclose()
        if self._owns_grpc_channel and self._grpc_channel is not None:
            await self._grpc_channel.close()

    async def __aenter__(self) -> DaprMQClient:
        return self

    async def __aexit__(
        self, exc_type: type[BaseException] | None, exc: BaseException | None, tb: TracebackType | None
    ) -> None:
        await self.aclose()

    def _path(self, queue_id: str, suffix: str) -> str:
        return f"{self._http_base_url}/queue/{quote(queue_id, safe='')}/{suffix}"

    async def _post_json(self, path: str, body: dict[str, Any], lease_id: str | None) -> httpx.Response:
        headers = {"lease-id": lease_id} if lease_id is not None else None
        return await self._http_client.post(path, json=body, headers=headers)

    @staticmethod
    def _try_parse_json(response: httpx.Response) -> dict[str, Any] | None:
        if not response.content:
            return None
        try:
            return response.json()
        except ValueError:
            return None

    def _require_parsed(self, response: httpx.Response) -> dict[str, Any]:
        parsed = self._try_parse_json(response)
        if parsed is None:
            raise DaprMQError(f"Empty or malformed response body from {response.request.url}")
        return parsed

    def _error_message_from(self, response: httpx.Response) -> str:
        body = self._try_parse_json(response)
        return (body or {}).get("message") or f"Request failed with status {response.status_code}"

    async def _map_generic_error(self, response: httpx.Response) -> DaprMQError:
        message = self._error_message_from(response)
        if response.status_code == 400:
            return ValidationError(message)
        if response.status_code == 404:
            return ActorNotFoundError(message)
        return DaprMQError(message)

    @staticmethod
    def _map_lock_error(error_code: str | None, message: str) -> DaprMQError:
        if error_code == "LOCK_NOT_FOUND":
            return LockNotFoundError(message)
        if error_code == "LOCK_EXPIRED":
            return LockExpiredError(message)
        if error_code == "SESSION_LEASE_EXPIRED":
            return SessionLeaseExpiredError(message)
        if error_code == "INVALID_LEASE_ID":
            return InvalidLeaseIdError(message)
        if error_code in ("INVALID_LOCK_ID", "INVALID_TTL"):
            return ValidationError(message)
        return DaprMQError(message, error_code)

    @staticmethod
    def _map_session_error(error_code: str, message: str) -> DaprMQError:
        if error_code == "SESSION_NOT_FOUND":
            return SessionNotFoundError(message)
        if error_code == "SESSION_LOCKED":
            return SessionLockedError(message)
        if error_code == "NO_SESSIONS_AVAILABLE":
            return NoSessionsAvailableError(message)
        if error_code == "SESSION_ACTOR_UNAVAILABLE":
            return SessionActorUnavailableError(message)
        return DaprMQError(message, error_code)
