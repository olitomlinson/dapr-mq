// API request/response types matching the DaprMQ API
export interface PushItem {
  item: unknown;
  priority: number;
}

export interface PushRequest {
  items: PushItem[];
}

export interface PopResponse {
  item: unknown;
  priority?: number;
  locked?: boolean;
  lockId?: string;
  lockExpiresAt?: number;
}

export interface AcknowledgeRequest {
  lockId: string;
}

export interface DeadLetterRequest {
  lockId: string;
}

export interface DeadLetterResponse {
  dlqActorId: string;
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
  dlqActorId?: string;
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
