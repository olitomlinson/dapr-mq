// API request/response types matching the DaprMQ API
export interface PushItem {
  item: unknown;
  priority: number;
}

export interface PushRequest {
  items: PushItem[];
}

export interface PopResponseItem {
  item: unknown;
  priority?: number;
  lockId?: string;
  lockExpiresAt?: number;
}

export interface PopResponse {
  items: PopResponseItem[];
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
export interface PoppedMessage {
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
