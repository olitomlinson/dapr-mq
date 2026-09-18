// Mirrors DaprMQ.ApiServer.Models.ApiModels.cs, client side. Field names match the server's
// camelCase JSON convention.

export interface EnqueueResponseWire {
  success: boolean;
  message: string;
  itemsEnqueued: number;
  itemsDeduplicated?: number;
}

export interface DequeueLockedItemWire {
  item: unknown;
  priority: number;
  lockId: string;
  lockExpiresAt: number;
}

export interface DequeueLockedResponseWire {
  items: DequeueLockedItemWire[];
  locked: boolean;
  message?: string;
}

export interface LockedResponseWire {
  message?: string;
  lockExpiresAt?: number;
}

export interface AcknowledgeResponseWire {
  success: boolean;
  message: string;
  itemsAcknowledged?: number;
  errorCode?: string;
}

export interface ExtendLockResponseWire {
  newExpiresAt: number;
  lockId: string;
}

export interface DeadLetterResponseWire {
  success: boolean;
  message: string;
  errorCode?: string;
  dlqId?: string;
}

export interface ErrorResponseWire {
  message: string;
  success?: boolean;
}

export interface AcceptSessionResponseWire {
  sessionId: string;
  leaseId: string;
  leaseExpiresAt: number;
}

export interface RenewSessionLeaseResponseWire {
  newExpiresAt: number;
}

export interface ReleaseSessionResponseWire {
  success: boolean;
}
