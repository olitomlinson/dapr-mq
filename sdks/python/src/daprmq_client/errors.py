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
