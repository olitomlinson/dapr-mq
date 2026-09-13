export const generateTopicId = (): string => {
  const chars = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let id = 'topic-';
  for (let i = 0; i < 6; i++) {
    id += chars[Math.floor(Math.random() * chars.length)];
  }
  return id;
};

export const validateTopicId = (id: string): { valid: boolean; error?: string } => {
  if (!id || id.trim().length === 0) {
    return { valid: false, error: 'Topic ID cannot be empty' };
  }
  if (id.length > 64) {
    return { valid: false, error: 'Topic ID must be 64 characters or less' };
  }
  if (!/^[a-zA-Z0-9_-]+$/.test(id)) {
    return { valid: false, error: 'Topic ID can only contain letters, numbers, hyphens, and underscores' };
  }
  return { valid: true };
};

export const validateSubscriberId = (id: string): { valid: boolean; error?: string } => {
  if (!id || id.trim().length === 0) {
    return { valid: false, error: 'Subscriber ID cannot be empty' };
  }
  if (id.length > 64) {
    return { valid: false, error: 'Subscriber ID must be 64 characters or less' };
  }
  if (!/^[a-zA-Z0-9_-]+$/.test(id)) {
    return { valid: false, error: 'Subscriber ID can only contain letters, numbers, hyphens, and underscores' };
  }
  return { valid: true };
};

export const updateTopicIdInUrl = (topicId: string): void => {
  const url = new URL(window.location.href);
  url.searchParams.set('topic_name', topicId);
  window.history.pushState({}, '', url.toString());
};

/** Mirrors TopicActor.BuildSubscriberQueueActorId server-side - ListSubscribers only returns
 * subscriber ids, not their provisioned queue ids, so the dashboard reconstructs it the same way. */
export const buildSubscriberQueueActorId = (topicId: string, subscriberId: string): string =>
  `${topicId}-sub-${subscriberId}`;
