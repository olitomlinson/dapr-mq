from __future__ import annotations

from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from typing import Any


@dataclass
class EnqueueItem:
    item: Any
    priority: int = 1
    idempotency_key: str | None = None
    session_id: str | None = None


@dataclass
class EnqueueResult:
    success: bool
    message: str
    items_enqueued: int
    items_deduplicated: int


@dataclass
class DequeueLockedItem:
    item: Any
    priority: int
    lock_id: str
    lock_expires_at: float


@dataclass
class DequeueLockedResult:
    items: list[DequeueLockedItem]
    locked: bool
    message: str | None = None


class AcknowledgeOutcome:
    """Per-lock outcomes of a batch acknowledge."""

    ACKNOWLEDGED = "ACKNOWLEDGED"
    """Settled by this call."""
    LOCK_NOT_FOUND = "LOCK_NOT_FOUND"
    """No such lock: never existed, or already settled. After the client retried a batch whose
    outcome was unknown, this can mean the earlier attempt acknowledged it."""
    LOCK_EXPIRED = "LOCK_EXPIRED"
    """Plain queue only: the lock's TTL passed, so the item is returning to the queue."""
    INVALID_LOCK_ID = "INVALID_LOCK_ID"
    """Empty lock id."""


@dataclass
class LockAcknowledgeResult:
    lock_id: str
    outcome: str
    """One of the ``AcknowledgeOutcome`` values."""


@dataclass
class AcknowledgeBatchResult:
    items_acknowledged: int
    results: list[LockAcknowledgeResult]
    """One entry per requested lock, in request order."""


@dataclass
class NackResult:
    dead_lettered: bool
    """True if the nack exceeded the server's max delivery count, so the item was dead-lettered."""
    delivery_count: int
    dlq_id: str | None = None


@dataclass
class SessionLease:
    session_id: str
    lease_id: str
    lease_expires_at: float


@dataclass
class SessionDelivery:
    """One delivered, locked item from consume_session.

    There is no lease_id here - the ConsumeSession wire protocol never exposes one to the client
    (the server tracks the lease internally and applies it when it calls Acknowledge/DeadLetter/Nack
    on the caller's behalf), so ack()/dead_letter()/nack() are the only way to resolve this item.
    """

    session_id: str
    lock_id: str
    item: Any
    priority: int
    lock_expires_at: float
    ack: Callable[[], Awaitable[None]]
    dead_letter: Callable[[], Awaitable[None]]
    nack: Callable[[], Awaitable[None]]
    """Returns the item to the front of the session for redelivery."""


@dataclass
class QueueDelivery:
    """One delivered, locked item from :meth:`DaprMQClient.consume`. The server renews its lock
    until it is settled. A rejected settlement arrives later through ``on_settle_failed``; the
    awaitables here only cover sending the frame."""

    lock_id: str
    item: Any
    priority: int
    lock_expires_at: float
    delivery_count: int
    """1 on a first delivery, 2 on the first redelivery after a nack or a lapsed lock, and so on."""
    ack: Callable[[], Awaitable[None]]
    nack: Callable[[], Awaitable[None]]
    """Returns the item to its original position for redelivery (+1 delivery count)."""
    dead_letter: Callable[[], Awaitable[None]]


@dataclass(frozen=True)
class RetryOptions:
    """How calls ride out a DaprMQ that can't serve them yet (sdks/testing/RETRIES_AND_READINESS.md)."""

    #: Seconds one call may keep retrying a DaprMQ that can't serve it (also sent to the server as
    #: its retry window). Never cuts a call that was delivered short. 0 turns client retries off.
    timeout: float = 30.0
    #: Give each enqueued item without an idempotency key a fresh one, so an enqueue whose outcome
    #: is unknown is retried safely. Costs the server one extra state write per item.
    auto_idempotency_keys: bool = False
    #: Tuning (normally left alone): no attempt starts with less than this many seconds left, since
    #: the server takes ~5 s to report it can't serve; and the backoff bounds.
    min_attempt_window: float = 6.0
    initial_backoff: float = 0.1
    max_backoff: float = 2.0
