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

/**
 * A QueueDelivery was settled after its consume stream closed. The server has already returned the
 * item to the queue (or will once its lock lapses), so it will be redelivered.
 */
export class StreamClosedError extends DaprMQError {
  constructor(message: string) {
    super(message, "STREAM_CLOSED");
    this.name = "StreamClosedError";
  }
}

/**
 * The operation was certainly not performed - DaprMQ couldn't serve it - and retrying ran out of
 * `RetryOptions.timeoutMs`. Always safe to repeat later.
 */
export class DaprMQUnavailableError extends DaprMQError {
  constructor(
    message: string,
    readonly operation: string,
    readonly queueId: string,
  ) {
    super(message, "UNAVAILABLE");
    this.name = "DaprMQUnavailableError";
  }
}

/**
 * The operation may or may not have been performed (e.g. the connection broke after it was sent),
 * and it isn't safe to repeat automatically. See sdks/testing/RETRIES_AND_READINESS.md for what to do
 * per operation; an enqueue whose items all carry an idempotency key is retried instead.
 */
export class DeliveryUnknownError extends DaprMQError {
  constructor(
    message: string,
    readonly operation: string,
    readonly queueId: string,
    /** For enqueue: each item's key, in order (undefined where the item had none). */
    readonly idempotencyKeys: (string | undefined)[] = [],
  ) {
    super(message, "DELIVERY_UNKNOWN");
    this.name = "DeliveryUnknownError";
  }
}
