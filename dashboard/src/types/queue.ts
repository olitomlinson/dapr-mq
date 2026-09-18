// API request/response types matching the DaprMQ API
export interface EnqueueItem {
  item: unknown;
  priority: number;
  idempotencyKey?: string;
}

export interface EnqueueRequest {
  items: EnqueueItem[];
}

export interface EnqueueResponse {
  success: boolean;
  message: string;
  itemsEnqueued: number;
  itemsDeduplicated?: number;
}

export interface DequeueResponseItem {
  item: unknown;
  priority?: number;
  lockId?: string;
  lockExpiresAt?: number;
}

export interface DequeueResponse {
  items: DequeueResponseItem[];
  locked?: boolean;
  message?: string;
}

export interface DequeueLockedResponseItem {
  item: unknown;
  priority: number;
  lockId: string;
  lockExpiresAt: number;
}

export interface DequeueLockedResponse {
  items: DequeueLockedResponseItem[];
  locked?: boolean;
  message?: string;
}

export interface AcknowledgeRequest {
  lockId: string;
}

export interface DeadLetterRequest {
  lockId: string;
}

export interface DeadLetterResponse {
  dlqId: string;
}

// UI state types
export interface DequeuedMessage {
  item: unknown;
  priority?: number;
  locked?: boolean;
  lockId?: string;
  lockExpiresAt?: number;
  acknowledged?: boolean;
  deadLettered?: boolean;
  dlqId?: string;
}

export interface ApiError {
  status: number | string;
  data: unknown;
}

// Utility types
export interface QueuePayload {
  userId: number;
  action: string;
  timestamp: string;
}

// HTTP Sink types
export interface RegisterSinkRequest {
  url: string;
  maxConcurrency: number;
  lockTtlSeconds: number;
  pollingIntervalSeconds: number;
}

export interface RegisterSinkResponse {
  sinkActorId: string;
}

export interface SinkConfig {
  url: string;
  maxConcurrency: number;
  lockTtlSeconds: number;
  pollingIntervalSeconds: number;
}

// WireMock types
export interface WiremockRequestItem {
  item: unknown;
  priority?: number;
  lockId?: string;
  lockExpiresAt?: number;
}

export interface WiremockRequest {
  id: string;
  request: {
    method: string;
    url: string;
    body: string;
    bodyAsBase64?: string;
    loggedDateString: string;
    headers: Record<string, string | string[]>;
  };
  response: {
    status: number;
    body: string;
  };
  parsedItems?: WiremockRequestItem[]; // Parsed from request body when available
}

export interface WiremockAdminResponse {
  requests: WiremockRequest[];
  meta: {
    total: number;
  };
}
