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
