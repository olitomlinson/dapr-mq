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


@dataclass
class SessionLease:
    session_id: str
    lease_id: str
    lease_expires_at: float


@dataclass
class SessionDelivery:
    """One delivered, locked item from consume_session.

    There is no lease_id here - the ConsumeSession wire protocol never exposes one to the client
    (the server tracks the lease internally and applies it when it calls Acknowledge/DeadLetter
    on the caller's behalf), so ack()/dead_letter() are the only way to resolve this item.
    """

    session_id: str
    lock_id: str
    item: Any
    priority: int
    lock_expires_at: float
    ack: Callable[[], Awaitable[None]]
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
