import { API_BASE, QueueApiError } from './queueApi';
import type {
  PublishRequest,
  PublishResponse,
  SubscribeResponse,
  ListSubscribersResponse,
  PublishStatusResponse,
  CircuitBreakerStatusResponse,
} from '../types/topic';

export const topicApi = {
  async publish(topicId: string, request: PublishRequest): Promise<PublishResponse> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/publish`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async subscribe(topicId: string, subscriberId: string): Promise<SubscribeResponse> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/subscribers/${subscriberId}`, {
      method: 'POST',
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async unsubscribe(topicId: string, subscriberId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/subscribers/${subscriberId}`, {
      method: 'DELETE',
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }
  },

  async listSubscribers(topicId: string): Promise<ListSubscribersResponse> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/subscribers`);

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async getPublishStatus(topicId: string, publishId: string): Promise<PublishStatusResponse | null> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/publish/${publishId}`);

    if (response.status === 404) {
      return null;
    }

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },

  async resetCircuitBreaker(topicId: string, subscriberId: string): Promise<void> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/subscribers/${subscriberId}/reset-circuit-breaker`, {
      method: 'POST',
    });

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }
  },

  async getCircuitBreakerStatus(topicId: string, subscriberId: string): Promise<CircuitBreakerStatusResponse | null> {
    const response = await fetch(`${API_BASE}/topic/${topicId}/subscribers/${subscriberId}/circuit-breaker`);

    if (response.status === 404) {
      return null;
    }

    if (!response.ok) {
      const data = await response.json().catch(() => ({ message: 'Unknown error' }));
      throw new QueueApiError(response.status, data);
    }

    return response.json();
  },
};
