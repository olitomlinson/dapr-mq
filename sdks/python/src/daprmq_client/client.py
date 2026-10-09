from __future__ import annotations

import asyncio
import json
import math
import random
import time
import uuid
from collections.abc import Callable
from collections.abc import AsyncIterator, Iterable
from types import TracebackType
from typing import Any
from urllib.parse import quote

import grpc
import grpc.aio
import httpx
from grpc_health.v1 import health_pb2, health_pb2_grpc

from .errors import (
    DaprMQUnavailableError,
    DeliveryUnknownError,
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
    StreamClosedError,
    ValidationError,
)
from .grpc import daprmq_pb2, daprmq_pb2_grpc
from .types import AcknowledgeBatchResult, DequeueLockedItem, LockAcknowledgeResult, DequeueLockedResult, EnqueueItem, EnqueueResult, NackResult, QueueDelivery, RetryOptions, SessionDelivery, SessionLease

#: How long a stopping :meth:`DaprMQClient.consume_session` stream waits, after half-closing, for the server
#: to apply what it was sent and end the stream, before it cancels the call.
SESSION_DRAIN_TIMEOUT_SECONDS = 5.0

#: The health service :meth:`DaprMQClient.wait_for_ready` watches by default: queue operations can be served.
OPERATIONS_HEALTH_SERVICE = "daprmq.DaprMQ.operations"

_DELIVERY_MARKER = "daprmq-delivery"
_RETRY_TIMEOUT_HEADER = "daprmq-retry-timeout"
_NOT_DELIVERED = "not-delivered"
_UNKNOWN = "unknown"


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
        retry: RetryOptions | None = None,
        health_stub: Any | None = None,
    ) -> None:
        self._http_base_url = http_base_url.rstrip("/")
        self._retry = retry or RetryOptions()
        self._health_stub = health_stub
        # httpx's own default (5 s) would cut a slow but progressing call short; match the server's
        # per-call safety limit instead (DELIVERY_ATTEMPT_MAX_SECONDS, 100 s).
        self._http_client = http_client or httpx.AsyncClient(timeout=httpx.Timeout(100.0, connect=10.0))
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
            if self._health_stub is None:
                self._health_stub = health_pb2_grpc.HealthStub(self._grpc_channel)

    async def enqueue(self, queue_id: str, items: Iterable[EnqueueItem]) -> EnqueueResult:
        items = list(items)  # iterated twice below; a generator would come up empty the second time
        # Keys are fixed before the first attempt, so a retry re-sends the same ones.
        keys = [
            i.idempotency_key or (uuid.uuid4().hex if self._retry.auto_idempotency_keys else None) for i in items
        ]
        body = {
            "items": [
                {
                    "item": i.item,
                    "priority": i.priority,
                    "idempotencyKey": key,
                    "sessionId": i.session_id,
                }
                for i, key in zip(items, keys)
            ]
        }

        # An unknown outcome is only safe to repeat when the server can de-duplicate every item.
        response = await self._send(
            "enqueue", queue_id, "POST", self._path(queue_id, "enqueue"), json_body=body,
            unknown_is_retryable=all(k is not None for k in keys), idempotency_keys=keys,
        )
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
        allow_competing_consumers: bool = False,
    ) -> DequeueLockedResult | None:
        headers = {"require-ack": "true", "count": str(count), "ttl-seconds": str(ttl_seconds)}
        if lease_id is not None:
            headers["lease-id"] = lease_id
        if allow_competing_consumers:
            headers["allow-competing-consumers"] = "true"

        response = await self._send("dequeue_locked", queue_id, "POST", self._path(queue_id, "dequeue"), headers=headers)

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
        response = await self._post_json("acknowledge", queue_id, self._path(queue_id, "acknowledge"), {"lockId": lock_id}, lease_id)
        if response.is_success:
            return

        body = self._try_parse_json(response)
        error_code = (body or {}).get("errorCode")
        message = (body or {}).get("message") or self._error_message_from(response)
        raise self._map_lock_error(error_code, message)

    async def acknowledge_batch(
        self, queue_id: str, lock_ids: list[str], *, lease_id: str | None = None
    ) -> AcknowledgeBatchResult:
        """Acknowledges up to 1,000 locks in one call, with an outcome per lock.

        One lock that expired or was already settled does not fail the rest; only a whole-call
        failure (bad lease, invalid request) raises. An unknown outcome is retried automatically,
        since re-sending is harmless; after such a retry, ``LOCK_NOT_FOUND`` can mean "already settled".
        """
        response = await self._send(
            "acknowledge_batch", queue_id, "POST", self._path(queue_id, "acknowledge-batch"),
            json_body={"lockIds": list(lock_ids)},
            headers={"lease-id": lease_id} if lease_id is not None else None,
            unknown_is_retryable=True,
        )
        body = self._try_parse_json(response) or {}
        if response.is_success:
            return AcknowledgeBatchResult(
                items_acknowledged=body.get("itemsAcknowledged", 0),
                results=[LockAcknowledgeResult(lock_id=r["lockId"], outcome=r["outcome"]) for r in body.get("results") or []],
            )

        message = body.get("message") or self._error_message_from(response)
        raise self._map_lock_error(body.get("errorCode"), message)

    async def extend_lock(
        self, queue_id: str, lock_id: str, additional_ttl_seconds: int, *, lease_id: str | None = None
    ) -> None:
        response = await self._post_json(
            "extend_lock",
            queue_id,
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
        response = await self._post_json("dead_letter", queue_id, self._path(queue_id, "deadletter"), {"lockId": lock_id}, lease_id)
        if response.is_success:
            return

        body = self._try_parse_json(response)
        error_code = (body or {}).get("errorCode")
        message = (body or {}).get("message") or self._error_message_from(response)
        raise self._map_lock_error(error_code, message)

    async def nack(self, queue_id: str, lock_id: str, *, lease_id: str | None = None) -> NackResult:
        """Returns a locked item to its original position in the queue.

        Counts as a delivery attempt: past the server's max delivery count the item is
        dead-lettered instead (``NackResult.dead_lettered``).
        """
        response = await self._post_json("nack", queue_id, self._path(queue_id, "nack"), {"lockId": lock_id}, lease_id)
        body = self._try_parse_json(response) or {}
        if response.is_success:
            return NackResult(
                dead_lettered=body.get("deadLettered", False),
                delivery_count=body.get("deliveryCount", 0),
                dlq_id=body.get("dlqId"),
            )

        message = body.get("message") or self._error_message_from(response)
        raise self._map_lock_error(body.get("errorCode"), message)

    async def accept_session(
        self, queue_id: str, *, session_id: str | None = None, lease_seconds: int = 30
    ) -> SessionLease | None:
        response = await self._post_json(
            "accept_session", queue_id, self._path(queue_id, "sessions/accept"),
            {"sessionId": session_id, "leaseSeconds": lease_seconds}, None,
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
            "renew_session_lease",
            queue_id,
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
            "release_session", queue_id, self._path(queue_id, f"sessions/{quote(session_id, safe='')}/release"),
            {"leaseId": lease_id}, None,
        )
        if not response.is_success:
            raise InvalidLeaseIdError(self._error_message_from(response))

    async def consume_session(
        self,
        queue_id: str,
        *,
        session_id: str | None = None,
        lease_seconds: int = 30,
        prefetch_count: int = 1,
        cancel: asyncio.Event | None = None,
    ) -> AsyncIterator[SessionDelivery]:
        """Managed consume loop for exactly one session: claims a session (any-available or
        targeted), streams delivered items back, and lets the caller ack/dead_letter each one. No
        lease_id is exposed here (unlike the unary session API) - the server tracks the lease
        internally and the gRPC call itself renews it for as long as the stream stays open.

        Pass `cancel` (an `asyncio.Event`) to stop the stream early. However the stream stops
        (`cancel`, leaving the loop, or an error), it half-closes rather than cancelling the call:
        the server applies every settlement already sent, then ends the stream and releases the
        session. Nothing more is handed out meanwhile, settling afterwards raises, and the call is
        cancelled only if the server hasn't ended it within ``SESSION_DRAIN_TIMEOUT_SECONDS``.
        After ``break``, close the generator (``contextlib.aclosing``) to wait for that.
        """
        call = self._grpc_stub.ConsumeSession()
        loop = asyncio.get_running_loop()
        write_lock = asyncio.Lock()  # a stop can now race an ack
        half_closed = False
        drain_expired = False
        drain_timer: asyncio.TimerHandle | None = None
        ended = False

        def expire_drain() -> None:
            nonlocal drain_expired
            drain_expired = True
            call.cancel()

        async def write(request: daprmq_pb2.ConsumeSessionRequest) -> None:
            async with write_lock:
                if half_closed:
                    raise DaprMQError("The session stream is closing; this message can no longer be settled on it.")
                await call.write(request)

        async def half_close() -> None:
            nonlocal half_closed, drain_timer
            async with write_lock:
                if half_closed:
                    return
                half_closed = True
                drain_timer = loop.call_later(SESSION_DRAIN_TIMEOUT_SECONDS, expire_drain)
                try:
                    await call.done_writing()
                except Exception:
                    pass  # best-effort - the stream may already be broken

        watcher: asyncio.Task[None] | None = None
        if cancel is not None:

            async def _watch() -> None:
                await cancel.wait()
                await half_close()

            watcher = asyncio.ensure_future(_watch())

        try:
            start = daprmq_pb2.ConsumeSessionStart(queue_id=queue_id, lease_seconds=lease_seconds, prefetch_count=prefetch_count)
            if session_id is not None:
                start.session_id = session_id
            await write(daprmq_pb2.ConsumeSessionRequest(start=start))

            assigned_session_id = session_id or ""

            async for response in call:
                if half_closed:
                    continue  # stopping: let the server finish, but hand out nothing more

                payload = response.WhichOneof("payload")

                if payload == "session_assigned":
                    assigned_session_id = response.session_assigned.session_id

                elif payload == "delivered":
                    delivered = response.delivered
                    lock_id = delivered.lock_id

                    async def ack(lock_id: str = lock_id) -> None:
                        await write(daprmq_pb2.ConsumeSessionRequest(ack=daprmq_pb2.ConsumeSessionAck(lock_id=lock_id)))

                    async def dead_letter(lock_id: str = lock_id) -> None:
                        await write(daprmq_pb2.ConsumeSessionRequest(dead_letter=daprmq_pb2.ConsumeSessionDeadLetter(lock_id=lock_id)))

                    async def nack(lock_id: str = lock_id) -> None:
                        await write(daprmq_pb2.ConsumeSessionRequest(nack=daprmq_pb2.ConsumeSessionNack(lock_id=lock_id)))

                    yield SessionDelivery(
                        session_id=assigned_session_id,
                        lock_id=lock_id,
                        item=json.loads(delivered.item_json),
                        priority=delivered.priority,
                        lock_expires_at=delivered.lock_expires_at,
                        ack=ack,
                        dead_letter=dead_letter,
                        nack=nack,
                    )

                elif payload == "error":
                    raise self._map_session_error(response.error.error_code, response.error.message)

                elif payload == "session_lost":
                    raise SessionLostError(response.session_lost.message)
            ended = True
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            ended = True
            if not drain_expired:
                raise  # cancelling the call ends its read this way; anything else is real
        finally:
            if watcher is not None:
                watcher.cancel()
            try:
                if not ended:
                    # However the stream stopped, let the server apply what it was sent first.
                    await half_close()
                    try:
                        async for _ in call:
                            pass
                    except asyncio.CancelledError:
                        if not drain_expired:
                            call.cancel()
                            raise
                    except Exception:
                        pass  # the stream had already failed
            finally:
                if drain_timer is not None:
                    drain_timer.cancel()

    async def consume(
        self,
        queue_id: str,
        *,
        prefetch_count: int = 1,
        lock_ttl_seconds: float = 30,
        allow_competing_consumers: bool = False,
        on_settle_failed: Callable[[str, DaprMQError], None] | None = None,
        cancel: asyncio.Event | None = None,
    ) -> AsyncIterator[QueueDelivery]:
        """Opens a ``Consume`` stream on a plain queue: the server keeps up to ``prefetch_count``
        locked items delivered (1-1000), refills as they are settled, and renews their locks, so
        ``lock_ttl_seconds`` (sent in whole seconds, rounded up) only bounds how long an item stays
        locked after this client disappears. Above 1, a nack can reorder the queue.

        ``on_settle_failed(lock_id, error)`` is called when the server rejects an ack, nack or
        dead-letter; the stream carries on. Stopping works as in :meth:`consume_session`: set
        ``cancel`` or leave the loop, and the stream half-closes, so the server applies every
        settlement already sent and returns the unsettled items straight away. Settling afterwards
        raises :class:`StreamClosedError`.
        """
        call = self._grpc_stub.Consume()
        loop = asyncio.get_running_loop()
        write_lock = asyncio.Lock()
        half_closed = False
        drain_expired = False
        drain_timer: asyncio.TimerHandle | None = None
        ended = False

        def expire_drain() -> None:
            nonlocal drain_expired
            drain_expired = True
            call.cancel()

        async def write(request: daprmq_pb2.ConsumeRequest) -> None:
            async with write_lock:
                if half_closed or ended:
                    raise StreamClosedError("The Consume stream is closed; this item can no longer be settled on it.")
                await call.write(request)

        async def half_close() -> None:
            nonlocal half_closed, drain_timer
            async with write_lock:
                if half_closed:
                    return
                half_closed = True
                drain_timer = loop.call_later(SESSION_DRAIN_TIMEOUT_SECONDS, expire_drain)
                try:
                    await call.done_writing()
                except Exception:
                    pass  # best-effort - the stream may already be broken

        watcher: asyncio.Task[None] | None = None
        if cancel is not None:

            async def _watch() -> None:
                await cancel.wait()
                await half_close()

            watcher = asyncio.ensure_future(_watch())

        try:
            start = daprmq_pb2.ConsumeStart(
                queue_id=queue_id,
                prefetch_count=max(prefetch_count, 1),
                lock_ttl_seconds=math.ceil(lock_ttl_seconds) if lock_ttl_seconds > 0 else 30,
                allow_competing_consumers=allow_competing_consumers,
            )
            await write(daprmq_pb2.ConsumeRequest(start=start))

            async for response in call:
                if half_closed:
                    continue  # stopping: let the server finish, but hand out nothing more

                payload = response.WhichOneof("payload")

                if payload == "delivered":
                    delivered = response.delivered
                    lock_id = delivered.lock_id

                    async def ack(lock_id: str = lock_id) -> None:
                        await write(daprmq_pb2.ConsumeRequest(ack=daprmq_pb2.ConsumeAck(lock_id=lock_id)))

                    async def nack(lock_id: str = lock_id) -> None:
                        await write(daprmq_pb2.ConsumeRequest(nack=daprmq_pb2.ConsumeNack(lock_id=lock_id)))

                    async def dead_letter(lock_id: str = lock_id) -> None:
                        await write(daprmq_pb2.ConsumeRequest(dead_letter=daprmq_pb2.ConsumeDeadLetter(lock_id=lock_id)))

                    yield QueueDelivery(
                        lock_id=lock_id,
                        item=json.loads(delivered.item_json),
                        priority=delivered.priority,
                        lock_expires_at=delivered.lock_expires_at,
                        delivery_count=delivered.delivery_count,
                        ack=ack,
                        nack=nack,
                        dead_letter=dead_letter,
                    )

                elif payload == "settle_failed":
                    if on_settle_failed is not None:
                        failed = response.settle_failed
                        on_settle_failed(failed.lock_id, self._map_lock_error(failed.error_code, failed.message))

                elif payload == "error":
                    raise self._map_lock_error(response.error.error_code, response.error.message)
            ended = True
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            ended = True
            if not drain_expired:
                raise  # cancelling the call ends its read this way; anything else is real
        finally:
            if watcher is not None:
                watcher.cancel()
            try:
                if not ended:
                    # However the stream stopped, let the server apply what it was sent first.
                    await half_close()
                    try:
                        async for _ in call:
                            pass
                    except asyncio.CancelledError:
                        if not drain_expired:
                            call.cancel()
                            raise
                    except Exception:
                        pass  # the stream had already failed
            finally:
                ended = True
                if drain_timer is not None:
                    drain_timer.cancel()

    async def wait_for_ready(self, service: str = OPERATIONS_HEALTH_SERVICE) -> None:
        """Wait until the server reports SERVING for ``service`` over the standard gRPC health
        protocol (grpc.health.v1.Health/Watch). The default means queue operations can be served
        end to end; ``"daprmq.DaprMQ"`` means just this server instance is ready. Reconnects while
        the server isn't listening; has no deadline of its own, so bound it with
        ``asyncio.wait_for`` / ``asyncio.timeout``.

        Raises ``NotImplementedError`` if the server doesn't expose the health service.
        """
        if self._health_stub is None:
            raise RuntimeError("This DaprMQClient was created without a gRPC channel or health stub.")

        backoff = 0.25
        while True:
            try:
                async for response in self._health_stub.Watch(health_pb2.HealthCheckRequest(service=service)):
                    if response.status == health_pb2.HealthCheckResponse.SERVING:
                        return
                    backoff = 0.25
                # Stream ended before SERVING (e.g. server shutting down) - reconnect.
            except grpc.aio.AioRpcError as e:
                if e.code() == grpc.StatusCode.UNIMPLEMENTED:
                    raise NotImplementedError(
                        "The DaprMQ server does not expose the gRPC health service; upgrade the server."
                    ) from e
                if e.code() != grpc.StatusCode.UNAVAILABLE:
                    raise
                # Server not listening yet - retry with backoff.
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 2.0)

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

    async def _post_json(
        self, operation: str, queue_id: str, path: str, body: dict[str, Any], lease_id: str | None
    ) -> httpx.Response:
        headers = {"lease-id": lease_id} if lease_id is not None else None
        return await self._send(operation, queue_id, "POST", path, json_body=body, headers=headers)

    async def _send(
        self,
        operation: str,
        queue_id: str,
        method: str,
        url: str,
        *,
        json_body: dict[str, Any] | None = None,
        headers: dict[str, str] | None = None,
        unknown_is_retryable: bool = False,
        idempotency_keys: list[str | None] | None = None,
    ) -> httpx.Response:
        """One REST call under the retry contract (sdks/testing/RETRIES_AND_READINESS.md):
        not-delivered failures are retried within the retry timeout, unknown outcomes only when
        ``unknown_is_retryable``. Every attempt tells the server how much retry time is left
        (daprmq-retry-timeout); it never cuts a delivered call short, which runs until the caller
        cancels or the HTTP client's own timeout. Returns any other response for the caller to map."""
        retry = self._retry
        retries = retry.timeout > 0
        deadline = time.monotonic() + retry.timeout
        backoff = retry.initial_backoff

        while True:
            remaining = deadline - time.monotonic()
            attempt_headers = dict(headers or {})
            if retries:
                attempt_headers[_RETRY_TIMEOUT_HEADER] = str(max(1, int(remaining * 1000)))

            not_delivered: bool
            try:
                response = await self._http_client.request(method, url, json=json_body, headers=attempt_headers or None)
                marker = response.headers.get(_DELIVERY_MARKER)
                if marker not in (_NOT_DELIVERED, _UNKNOWN):
                    return response
                not_delivered = marker == _NOT_DELIVERED
                reason = self._error_message_from(response)
            except (httpx.ConnectError, httpx.ConnectTimeout, httpx.PoolTimeout) as e:
                not_delivered, reason = True, str(e) or type(e).__name__  # nothing was sent
            except httpx.TransportError as e:
                not_delivered, reason = False, str(e) or type(e).__name__  # sent, then broke or timed out

            retryable = not_delivered or unknown_is_retryable
            delay = random.uniform(0, backoff)
            time_left = deadline - time.monotonic() - delay
            if not retries or not retryable or time_left < retry.min_attempt_window:
                if not_delivered:
                    raise DaprMQUnavailableError(
                        f"DaprMQ is unavailable; {operation} was not performed: {reason}", operation, queue_id
                    )
                raise DeliveryUnknownError(
                    f"The outcome of {operation} is unknown: it may or may not have been performed ({reason})",
                    operation,
                    queue_id,
                    idempotency_keys,
                )

            await asyncio.sleep(delay)
            backoff = min(backoff * 2, retry.max_backoff)

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
        if error_code in ("INVALID_LOCK_ID", "INVALID_TTL", "VALIDATION_ERROR", "INVALID_ARGUMENT"):
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
