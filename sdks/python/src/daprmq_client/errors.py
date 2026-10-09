class DaprMQError(Exception):
    def __init__(self, message: str, code: str | None = None) -> None:
        super().__init__(message)
        self.message = message
        self.code = code


class LockNotFoundError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "LOCK_NOT_FOUND")


class LockExpiredError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "LOCK_EXPIRED")


class ActorNotFoundError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "ACTOR_NOT_FOUND")


class ValidationError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "VALIDATION_ERROR")


class SessionNotFoundError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "SESSION_NOT_FOUND")


class SessionLockedError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "SESSION_LOCKED")


class SessionLeaseExpiredError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "SESSION_LEASE_EXPIRED")


class InvalidLeaseIdError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "INVALID_LEASE_ID")


class SessionActorUnavailableError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "SESSION_ACTOR_UNAVAILABLE")


class NoSessionsAvailableError(DaprMQError):
    def __init__(self, message: str) -> None:
        super().__init__(message, "NO_SESSIONS_AVAILABLE")


class SessionLostError(DaprMQError):
    """Raised by consume_session when a session was successfully claimed but its lease could not
    be maintained afterwards (server sent a terminal SessionLost frame) - distinct from a claim
    that never succeeded in the first place."""

    def __init__(self, message: str) -> None:
        super().__init__(message, "SESSION_LOST")


class StreamClosedError(DaprMQError):
    """A :class:`QueueDelivery` was settled after its ``consume`` stream closed. The server has
    already returned the item to the queue (or will once its lock lapses), so it will be redelivered."""

    def __init__(self, message: str) -> None:
        super().__init__(message, "STREAM_CLOSED")


class DaprMQUnavailableError(DaprMQError):
    """The operation was certainly not performed - DaprMQ couldn't serve it - and retrying ran out
    of ``RetryOptions.timeout``. Always safe to repeat later."""

    def __init__(self, message: str, operation: str, queue_id: str) -> None:
        super().__init__(message, "UNAVAILABLE")
        self.operation = operation
        self.queue_id = queue_id


class DeliveryUnknownError(DaprMQError):
    """The operation may or may not have been performed (e.g. the connection broke after it was
    sent), and it isn't safe to repeat automatically. See sdks/testing/RETRIES_AND_READINESS.md for
    what to do per operation; an enqueue whose items all carry an idempotency key is retried instead."""

    def __init__(self, message: str, operation: str, queue_id: str, idempotency_keys: list[str | None] | None = None) -> None:
        super().__init__(message, "DELIVERY_UNKNOWN")
        self.operation = operation
        self.queue_id = queue_id
        #: For enqueue: each item's key, in order (None where the item had none).
        self.idempotency_keys = idempotency_keys or []
