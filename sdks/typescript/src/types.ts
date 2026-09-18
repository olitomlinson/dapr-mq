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

export interface SessionLease {
  sessionId: string;
  leaseId: string;
  leaseExpiresAt: number;
}

/**
 * One delivered, locked item from consumeSession. There is no leaseId here - the ConsumeSession
 * wire protocol never exposes one to the client (the server tracks the lease internally and
 * applies it when it calls Acknowledge/DeadLetter on the caller's behalf), so ack/deadLetter are
 * the only way to resolve this item.
 */
export interface SessionDelivery {
  sessionId: string;
  lockId: string;
  item: unknown;
  priority: number;
  lockExpiresAt: number;
  ack(): Promise<void>;
  deadLetter(): Promise<void>;
}
