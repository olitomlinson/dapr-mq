// API request/response types matching the DaprMQ Topic (pub/sub) API

export interface PublishRequest {
  items: { item: unknown; priority: number; idempotencyKey?: string }[];
}

export interface PublishResponse {
  accepted: boolean;
  publishId: string;
  sequence: number;
}

export interface SubscribeRequest {
  dedupEnabled?: boolean;
}

export interface SubscribeResponse {
  success: boolean;
  queueActorId: string;
}

export interface ListSubscribersResponse {
  subscriberIds: string[];
}

export interface PublishStatusResponse {
  complete: boolean;
  targetSubscriberIds: string[];
  deliveredSubscriberIds: string[];
}

export interface CircuitBreakerStatusResponse {
  consecutiveFailures: number;
  firstFailureAt?: number;
  nextRetryAt?: number;
  blacklisted: boolean;
}
