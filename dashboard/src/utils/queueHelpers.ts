import type { QueuePayload } from '../types/queue';

export const generateQueueId = (): string => {
  const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let id = 'queue-';
  for (let i = 0; i < 6; i++) {
    id += chars[Math.floor(Math.random() * chars.length)];
  }
  return id;
};

export const generatePayload = (): QueuePayload => ({
  userId: Math.floor(Math.random() * 9000) + 1000,
  action: ['login', 'logout', 'purchase', 'signup'][Math.floor(Math.random() * 4)],
  timestamp: new Date().toISOString(),
});

export const getPriorityClassName = (priority?: number): string => {
  if (priority === 0) return 'priority0';
  if (priority === 1) return 'priority1';
  if (priority === 2) return 'priority2';
  return 'priorityDefault';
};

export const isDeadLetterQueue = (queueId: string): boolean => {
  return queueId.endsWith('-deadletter');
};

export const validateQueueId = (id: string): { valid: boolean; error?: string } => {
  if (!id || id.trim().length === 0) {
    return { valid: false, error: 'Queue ID cannot be empty' };
  }
  if (id.length > 64) {
    return { valid: false, error: 'Queue ID must be 64 characters or less' };
  }
  if (!/^[a-zA-Z0-9_-]+$/.test(id)) {
    return { valid: false, error: 'Queue ID can only contain letters, numbers, hyphens, and underscores' };
  }
  return { valid: true };
};

export const updateQueueIdInUrl = (queueId: string): void => {
  const url = new URL(window.location.href);
  url.searchParams.set('queue_name', queueId);
  window.history.pushState({}, '', url.toString());
};

export interface ObjectClaim {
  objectClaimToken: string;
  contentType?: string;
}

export const isObjectClaim = (item: unknown): item is ObjectClaim =>
  typeof item === 'object' &&
  item !== null &&
  typeof (item as Record<string, unknown>).objectClaimToken === 'string';

/** Truncates a long objectClaimToken for display, leaving the full value untouched for download URLs. */
export const withTruncatedClaimToken = (item: unknown): unknown =>
  isObjectClaim(item)
    ? { ...item, objectClaimToken: `${item.objectClaimToken.slice(0, 24)}…` }
    : item;

/** Same as withTruncatedClaimToken, but recurses into arrays/objects for display of arbitrary parsed JSON (e.g. a raw request body containing a batch of items). */
export const truncateClaimTokensDeep = (value: unknown): unknown => {
  if (Array.isArray(value)) {
    return value.map(truncateClaimTokensDeep);
  }
  if (isObjectClaim(value)) {
    return withTruncatedClaimToken(value);
  }
  if (typeof value === 'object' && value !== null) {
    return Object.fromEntries(
      Object.entries(value).map(([key, val]) => [key, truncateClaimTokensDeep(val)])
    );
  }
  return value;
};
