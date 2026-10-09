from __future__ import annotations

import asyncio
import contextlib
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from enum import Enum
from typing import Any, Protocol

from .types import QueueDelivery


class QueueHandlerFailureAction(str, Enum):
    NACK = "nack"
    """Return the message to its original position for redelivery (+1 delivery count)."""
    DEAD_LETTER = "dead_letter"


@dataclass
class QueueConsumerOptions:
    max_active_messages: int = 100
    """Delivered but unsettled messages the server keeps in flight to this consumer: the stream's
    prefetch_count (1-1000). Messages over max_concurrent_handlers wait locked, and the server keeps
    their locks alive."""
    max_concurrent_handlers: int = 0
    """Handlers running at once. 0 = unlimited, which max_active_messages bounds."""
    lock_ttl_seconds: float = 30
    """Passed to the stream; the server renews each lock until its message is settled."""
    allow_competing_consumers: bool = True
    """Lets replicas share the queue, each holding its own locks."""
    strict_order: bool = False
    """Handles messages one at a time in queue order, including after a nack: forces a window of 1,
    one handler, and no competing consumers."""
    on_handler_error: QueueHandlerFailureAction = QueueHandlerFailureAction.NACK
    max_retriable_errors_per_sec: float = 10
    """Paces nacks after handler errors, so a failing handler doesn't spin. 0 = unpaced."""
    min_backoff_seconds: float = 1
    max_backoff_seconds: float = 60
    """Reconnect backoff after the stream breaks, doubling to the max; resets after a delivery."""
    drain_timeout_seconds: float = 30.0
    """How long stop() waits for running handlers before cancelling them."""


@dataclass
class QueueMessageContext:
    queue_id: str
    lock_id: str
    item: Any
    priority: int
    delivery_count: int
    """1 on a first delivery, 2 on the first redelivery, and so on."""


class QueueCapableClient(Protocol):
    """The slice of DaprMQClient that QueueConsumer needs - satisfied by DaprMQClient itself."""

    def consume(
        self,
        queue_id: str,
        *,
        prefetch_count: int = 1,
        lock_ttl_seconds: float = 30,
        allow_competing_consumers: bool = False,
        cancel: asyncio.Event | None = None,
    ) -> Any: ...


class QueueConsumer:
    """Runs a handler over a plain queue's ``consume`` stream: success acks the message, an
    exception nacks or dead-letters it, and a broken stream is reopened with backoff (the server
    has already returned whatever was unsettled on it)."""

    def __init__(
        self,
        client: QueueCapableClient,
        queue_id: str,
        options: QueueConsumerOptions,
        handler: Callable[[QueueMessageContext], Awaitable[None]],
    ) -> None:
        self.client = client
        self.queue_id = queue_id
        self.options = options
        self.handler = handler

        # Test seam for the reconnect backoff and nack pacing.
        self.delay: Callable[[float], Awaitable[None]] = asyncio.sleep

        strict = options.strict_order
        self._prefetch_count = 1 if strict else max(options.max_active_messages, 1)
        self._allow_competing_consumers = not strict and options.allow_competing_consumers
        handler_limit = 1 if strict else options.max_concurrent_handlers
        self._slots = asyncio.Semaphore(handler_limit) if handler_limit > 0 else None

        self._stop_event = asyncio.Event()
        self._close_stream = asyncio.Event()
        self._handlers: set[asyncio.Task[None]] = set()
        self._task: asyncio.Task[None] | None = None
        self._next_nack = 0.0

    def start(self) -> None:
        self._task = asyncio.ensure_future(self._run())

    async def stop(self) -> None:
        """Stops handing out messages, lets running handlers finish and settle for up to
        drain_timeout_seconds (then cancels them), and closes the stream, so the server returns
        every message not yet handled straight away."""
        self._stop_event.set()
        if self._task is None:
            return

        running = list(self._handlers)
        if running:
            _, pending = await asyncio.wait(running, timeout=self.options.drain_timeout_seconds)
            for task in pending:
                task.cancel()  # left unsettled: returned when the stream closes
            await asyncio.gather(*running, return_exceptions=True)
        self._close_stream.set()
        await self._task

    async def __aenter__(self) -> QueueConsumer:
        self.start()
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.stop()

    async def _run(self) -> None:
        backoff_seconds = self.options.min_backoff_seconds

        while not self._stop_event.is_set():
            delivered = False
            # On stop the stream stays open until running handlers have settled on it; stop()
            # then sets this. Closing it under a handler would lose that message's ack.
            self._close_stream = asyncio.Event()
            try:
                stream = self.client.consume(
                    self.queue_id,
                    prefetch_count=self._prefetch_count,
                    lock_ttl_seconds=self.options.lock_ttl_seconds,
                    allow_competing_consumers=self._allow_competing_consumers,
                    cancel=self._close_stream,
                )
                async with contextlib.aclosing(stream):
                    async for delivery in stream:
                        delivered = True
                        if not await self._take_slot():
                            continue  # stopping: left unsettled, returned when the stream closes
                        task = asyncio.ensure_future(self._handle(delivery))
                        self._handlers.add(task)
                        task.add_done_callback(self._handlers.discard)
            except asyncio.CancelledError:
                raise
            except Exception:
                pass  # the stream broke: reopen (or stop) below

            await asyncio.gather(*list(self._handlers), return_exceptions=True)
            if self._stop_event.is_set():
                break

            if delivered:
                backoff_seconds = self.options.min_backoff_seconds
            if not await self._sleep_unless_stopped(backoff_seconds):
                break
            backoff_seconds = min(backoff_seconds * 2, self.options.max_backoff_seconds)

    async def _take_slot(self) -> bool:
        """Waits for a handler slot; False once stopping."""
        if self._stop_event.is_set():
            return False
        if self._slots is None:
            return True
        acquire = asyncio.ensure_future(self._slots.acquire())
        stopping = asyncio.ensure_future(self._stop_event.wait())
        await asyncio.wait({acquire, stopping}, return_when=asyncio.FIRST_COMPLETED)
        stopping.cancel()
        if not acquire.done():
            acquire.cancel()
            return False
        if self._stop_event.is_set():
            self._slots.release()
            return False
        return True

    async def _sleep_unless_stopped(self, seconds: float) -> bool:
        sleeping = asyncio.ensure_future(self.delay(seconds))
        stopping = asyncio.ensure_future(self._stop_event.wait())
        await asyncio.wait({sleeping, stopping}, return_when=asyncio.FIRST_COMPLETED)
        sleeping.cancel()
        stopping.cancel()
        return not self._stop_event.is_set()

    async def _handle(self, delivery: QueueDelivery) -> None:
        """Runs the handler and settles the message. Never raises: a settle that fails because the
        stream broke needs nothing more, as the server returns the message."""
        context = QueueMessageContext(
            queue_id=self.queue_id,
            lock_id=delivery.lock_id,
            item=delivery.item,
            priority=delivery.priority,
            delivery_count=delivery.delivery_count,
        )
        try:
            try:
                await self.handler(context)
            except asyncio.CancelledError:
                return  # cancelled at the drain timeout: left unsettled
            except Exception:
                if self._stop_event.is_set():
                    return  # stopping: left unsettled, returned when the stream closes
                if self.options.on_handler_error == QueueHandlerFailureAction.DEAD_LETTER:
                    await delivery.dead_letter()
                else:
                    await self._pace_nack()
                    await delivery.nack()
                return
            await delivery.ack()
        except asyncio.CancelledError:
            pass
        except Exception:
            pass
        finally:
            if self._slots is not None:
                self._slots.release()

    async def _pace_nack(self) -> None:
        """Waits for this nack's slot: at most max_retriable_errors_per_sec nacks a second."""
        rate = self.options.max_retriable_errors_per_sec
        if rate <= 0:
            return
        now = time.monotonic()
        slot = max(self._next_nack, now)
        self._next_nack = slot + 1 / rate
        if slot > now:
            await self.delay(slot - now)
