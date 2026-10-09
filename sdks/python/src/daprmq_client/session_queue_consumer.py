from __future__ import annotations

import asyncio
import contextlib
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Protocol

import grpc

from .errors import (
    NoSessionsAvailableError,
    SessionActorUnavailableError,
    SessionLockedError,
    SessionLostError,
    SessionNotFoundError,
)
from .types import SessionDelivery


class SessionHandlerFailureAction(str, Enum):
    DEAD_LETTER_MESSAGE = "dead_letter_message"
    ABANDON_SESSION = "abandon_session"
    BOTH = "both"
    NACK_MESSAGE = "nack_message"
    """Return the message to the front of the session for redelivery."""


@dataclass
class SessionQueueConsumerOptions:
    max_concurrent_sessions: int = 4
    """Sticky routing to one specific session. Must pair with max_concurrent_sessions == 1."""
    target_session_id: str | None = None
    lease_seconds: int = 30
    prefetch_count: int = 1
    session_idle_timeout_seconds: int = 0
    """Releases a session once no message has arrived for this long, so the slot moves on to the
    next one. 0 = the server default (lease_seconds)."""
    min_backoff_seconds: int = 1
    max_backoff_seconds: int = 60
    on_handler_exception: SessionHandlerFailureAction = SessionHandlerFailureAction.DEAD_LETTER_MESSAGE
    drain_timeout_seconds: float = 30.0


@dataclass
class SessionMessageContext:
    """No lease_id here (unlike the low-level unary session API) - the ConsumeSession wire
    protocol never exposes one to the client, since the server tracks the lease internally. See
    `SessionDelivery`."""

    queue_id: str
    session_id: str
    lock_id: str
    item: Any
    priority: int


@dataclass
class _Slot:
    handling: bool = False
    # Closes the slot's current stream: set when stopping begins if the slot is idle, otherwise the
    # slot leaves the stream once the running message is settled.
    close_stream: asyncio.Event = field(default_factory=asyncio.Event)


class SessionCapableClient(Protocol):
    """The slice of DaprMQClient that SessionQueueConsumer needs - satisfied by DaprMQClient itself."""

    def consume_session(
        self,
        queue_id: str,
        *,
        session_id: str | None = None,
        lease_seconds: int = 30,
        prefetch_count: int = 1,
        session_idle_timeout_seconds: int = 0,
        cancel: asyncio.Event | None = None,
    ) -> Any: ...


class SessionQueueConsumer:
    """Manages a pool of `max_concurrent_sessions` independent slots, each looping over a
    `consume_session` stream: claim a session, hand each delivered item to the caller's handler,
    ack/dead_letter it, and repeat until the session drains - then claim another. Backoff on a
    failed claim doubles on a miss, resets on a successful claim, and is capped.
    """

    def __init__(
        self,
        client: SessionCapableClient,
        queue_id: str,
        options: SessionQueueConsumerOptions,
        handler: Callable[[SessionMessageContext], Awaitable[None]],
    ) -> None:
        if options.target_session_id is not None and options.max_concurrent_sessions != 1:
            raise ValueError("target_session_id requires max_concurrent_sessions == 1.")

        self.client = client
        self.queue_id = queue_id
        self.options = options
        self.handler = handler

        # Test seam - substituted by tests to assert on requested backoff durations without
        # waiting real time.
        self.delay: Callable[[float], Awaitable[None]] = asyncio.sleep

        self._stop_event = asyncio.Event()
        self._slots = [_Slot() for _ in range(options.max_concurrent_sessions)]
        self._slot_tasks: list[asyncio.Task[None]] | None = None

    def start(self) -> None:
        self._slot_tasks = [asyncio.ensure_future(self._run_slot(slot)) for slot in self._slots]

    async def stop(self) -> None:
        """Stops claiming new sessions, lets in-flight handlers finish and settle for up to
        drain_timeout_seconds (then cancels them), and returns once every stream has closed -
        closing the stream is itself what releases the session, no separate release_session call
        is needed here. Prefetched, unhandled messages return with their session."""
        self._stop_event.set()
        for slot in self._slots:
            if not slot.handling:
                slot.close_stream.set()

        if self._slot_tasks:
            _, pending = await asyncio.wait(self._slot_tasks, timeout=self.options.drain_timeout_seconds)
            for task in pending:
                task.cancel()  # cancels the running handler
            if pending:
                await asyncio.wait(pending)

    async def __aenter__(self) -> SessionQueueConsumer:
        self.start()
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.stop()

    async def _run_slot(self, slot: _Slot) -> None:
        backoff_seconds = self.options.min_backoff_seconds

        while not self._stop_event.is_set():
            session_was_claimed = False
            slot.close_stream = asyncio.Event()
            try:
                stream = self.client.consume_session(
                    self.queue_id,
                    session_id=self.options.target_session_id,
                    lease_seconds=self.options.lease_seconds,
                    prefetch_count=self.options.prefetch_count,
                    session_idle_timeout_seconds=self.options.session_idle_timeout_seconds,
                    cancel=slot.close_stream,
                )
                # aclosing: leaving the loop waits for the stream to close, which releases the session.
                async with contextlib.aclosing(stream):
                    async for delivery in stream:
                        session_was_claimed = True
                        if self._stop_event.is_set():
                            break  # stopping: don't start another message, even a prefetched one
                        slot.handling = True
                        try:
                            await self._handle_delivery(delivery)
                        finally:
                            slot.handling = False
                        if self._stop_event.is_set():
                            break

                session_was_claimed = True  # stream ended cleanly after a successful claim (drained)
            except asyncio.CancelledError:
                raise
            except grpc.aio.AioRpcError as exc:
                if exc.code() == grpc.StatusCode.CANCELLED and self._stop_event.is_set():
                    break
                session_was_claimed = False
            except (NoSessionsAvailableError, SessionNotFoundError, SessionLockedError, SessionActorUnavailableError):
                pass  # claim itself failed - fall through to backoff below
            except SessionLostError:
                session_was_claimed = True  # claim succeeded; the lease was lost afterward
            except Exception:
                if self._stop_event.is_set():
                    break
                # Any other exception surfacing mid-stream - including a handler exception
                # re-raised by _handle_delivery under ABANDON_SESSION/BOTH - ends this slot's
                # current stream early. session_was_claimed is already true by the time the async
                # for body can raise, so the outer loop below retries immediately rather than
                # backing off as if the claim itself had failed.

            if self._stop_event.is_set():
                break

            if session_was_claimed:
                backoff_seconds = self.options.min_backoff_seconds
                continue

            try:
                await self.delay(backoff_seconds)
            except asyncio.CancelledError:
                break

            backoff_seconds = min(backoff_seconds * 2, self.options.max_backoff_seconds)

    async def _handle_delivery(self, delivery: SessionDelivery) -> None:
        context = SessionMessageContext(
            queue_id=self.queue_id,
            session_id=delivery.session_id,
            lock_id=delivery.lock_id,
            item=delivery.item,
            priority=delivery.priority,
        )
        try:
            await self.handler(context)
            await delivery.ack()
        except asyncio.CancelledError:
            raise
        except Exception:
            if self._stop_event.is_set():
                raise
            action = self.options.on_handler_exception
            if action in (SessionHandlerFailureAction.DEAD_LETTER_MESSAGE, SessionHandlerFailureAction.BOTH):
                await delivery.dead_letter()
            if action == SessionHandlerFailureAction.NACK_MESSAGE:
                await delivery.nack()
            if action in (SessionHandlerFailureAction.ABANDON_SESSION, SessionHandlerFailureAction.BOTH):
                raise
