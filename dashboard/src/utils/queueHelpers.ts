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
