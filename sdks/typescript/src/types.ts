export interface EnqueueItem {
  item: unknown;
  priority?: number;
  idempotencyKey?: string;
  sessionId?: string;
}

export interface EnqueueResult {
  success: boolean;
  message: string;
  itemsEnqueued: number;
  itemsDeduplicated: number;
}

export interface DequeueLockedItem {
  item: unknown;
  priority: number;
  lockId: string;
  lockExpiresAt: number;
}

export interface DequeueLockedResult {
  items: DequeueLockedItem[];
  locked: boolean;
  message?: string;
}

/** Per-lock outcomes of a batch acknowledge. */
export const AcknowledgeOutcome = {
  /** Settled by this call. */
  Acknowledged: "ACKNOWLEDGED",
  /**
   * No such lock: never existed, or already settled. After the client retried a batch whose outcome
   * was unknown, this can mean the earlier attempt acknowledged it.
   */
  LockNotFound: "LOCK_NOT_FOUND",
  /** Plain queue only: the lock's TTL passed, so the item is returning to the queue. */
  LockExpired: "LOCK_EXPIRED",
  /** Empty lock id. */
  InvalidLockId: "INVALID_LOCK_ID",
} as const;

export interface LockAcknowledgeResult {
  lockId: string;
  /** One of the AcknowledgeOutcome values. */
  outcome: string;
}

export interface AcknowledgeBatchResult {
  itemsAcknowledged: number;
  /** One entry per requested lock, in request order. */
  results: LockAcknowledgeResult[];
}

export interface NackResult {
  /** True if the nack exceeded the server's max delivery count, so the item was dead-lettered. */
  deadLettered: boolean;
  deliveryCount: number;
  dlqId?: string;
}

export interface SessionLease {
  sessionId: string;
  leaseId: string;
  leaseExpiresAt: number;
}

/**
 * One delivered, locked item from consumeSession. There is no leaseId here - the ConsumeSession
 * wire protocol never exposes one to the client (the server tracks the lease internally and
 * applies it when it calls Acknowledge/DeadLetter/Nack on the caller's behalf), so
 * ack/deadLetter/nack are the only way to resolve this item.
 */
export interface SessionDelivery {
  sessionId: string;
  lockId: string;
  item: unknown;
  priority: number;
  lockExpiresAt: number;
  ack(): Promise<void>;
  deadLetter(): Promise<void>;
  /** Returns the item to the front of the session for redelivery. */
  nack(): Promise<void>;
}
