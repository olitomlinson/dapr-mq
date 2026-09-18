export class DaprMQError extends Error {
  readonly code?: string;

  constructor(message: string, code?: string) {
    super(message);
    this.name = "DaprMQError";
    this.code = code;
  }
}

export class LockNotFoundError extends DaprMQError {
  constructor(message: string) {
    super(message, "LOCK_NOT_FOUND");
    this.name = "LockNotFoundError";
  }
}

export class LockExpiredError extends DaprMQError {
  constructor(message: string) {
    super(message, "LOCK_EXPIRED");
    this.name = "LockExpiredError";
  }
}

export class ActorNotFoundError extends DaprMQError {
  constructor(message: string) {
    super(message, "ACTOR_NOT_FOUND");
    this.name = "ActorNotFoundError";
  }
}

export class ValidationError extends DaprMQError {
  constructor(message: string) {
    super(message, "VALIDATION_ERROR");
    this.name = "ValidationError";
  }
}

export class SessionNotFoundError extends DaprMQError {
  constructor(message: string) {
    super(message, "SESSION_NOT_FOUND");
    this.name = "SessionNotFoundError";
  }
}

export class SessionLockedError extends DaprMQError {
  constructor(message: string) {
    super(message, "SESSION_LOCKED");
    this.name = "SessionLockedError";
  }
}

export class SessionLeaseExpiredError extends DaprMQError {
  constructor(message: string) {
    super(message, "SESSION_LEASE_EXPIRED");
    this.name = "SessionLeaseExpiredError";
  }
}

export class InvalidLeaseIdError extends DaprMQError {
  constructor(message: string) {
    super(message, "INVALID_LEASE_ID");
    this.name = "InvalidLeaseIdError";
  }
}

export class SessionActorUnavailableError extends DaprMQError {
  constructor(message: string) {
    super(message, "SESSION_ACTOR_UNAVAILABLE");
    this.name = "SessionActorUnavailableError";
  }
}

export class NoSessionsAvailableError extends DaprMQError {
  constructor(message: string) {
    super(message, "NO_SESSIONS_AVAILABLE");
    this.name = "NoSessionsAvailableError";
  }
}

/**
 * Thrown by consumeSession when a session was successfully claimed but its lease could not be
 * maintained afterwards (server sent a terminal SessionLost frame) - distinct from a claim that
 * never succeeded in the first place.
 */
export class SessionLostError extends DaprMQError {
  constructor(message: string) {
    super(message, "SESSION_LOST");
    this.name = "SessionLostError";
  }
}
