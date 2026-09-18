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
