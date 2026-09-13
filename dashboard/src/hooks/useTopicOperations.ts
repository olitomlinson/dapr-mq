import { useState, useEffect } from 'react';
import { topicApi } from '../services/topicApi';
import { QueueApiError, createApiError } from '../services/queueApi';
import type {
  PublishResponse,
  ListSubscribersResponse,
  PublishStatusResponse,
  CircuitBreakerStatusResponse,
} from '../types/topic';
import type { ApiError, QueuePayload } from '../types/queue';
import { generatePayload } from '../utils/queueHelpers';

export const useTopicOperations = (topicId: string) => {
  const [currentPayload, setCurrentPayload] = useState<QueuePayload>(() => generatePayload());
  const [isPublishing, setIsPublishing] = useState(false);
  const [isSubscribing, setIsSubscribing] = useState(false);
  const [subscriberIds, setSubscriberIds] = useState<string[]>([]);
  const [lastPublish, setLastPublish] = useState<PublishResponse | null>(null);
  const [error, setError] = useState<ApiError | null>(null);

  // Reset state when topic ID changes
  useEffect(() => {
    setCurrentPayload(generatePayload());
    setSubscriberIds([]);
    setLastPublish(null);
    setError(null);
  }, [topicId]);

  const handleError = (err: unknown) => {
    if (err instanceof QueueApiError) {
      setError(createApiError(err.status, err.data));
    } else {
      setError(createApiError('Network Error', (err as Error).message));
    }
  };

  const publish = async (priority: number, payload: QueuePayload) => {
    setIsPublishing(true);
    try {
      const response = await topicApi.publish(topicId, { items: [{ item: payload, priority }] });
      setLastPublish(response);
      setCurrentPayload(generatePayload());
      return response;
    } catch (err) {
      handleError(err);
      return null;
    } finally {
      setIsPublishing(false);
    }
  };

  const subscribe = async (subscriberId: string) => {
    setIsSubscribing(true);
    try {
      const response = await topicApi.subscribe(topicId, subscriberId);
      setSubscriberIds(prev => (prev.includes(subscriberId) ? prev : [...prev, subscriberId]));
      return response;
    } catch (err) {
      handleError(err);
      return null;
    } finally {
      setIsSubscribing(false);
    }
  };

  const unsubscribe = async (subscriberId: string) => {
    try {
      await topicApi.unsubscribe(topicId, subscriberId);
      setSubscriberIds(prev => prev.filter(id => id !== subscriberId));
    } catch (err) {
      handleError(err);
    }
  };

  const refreshSubscribers = async (): Promise<ListSubscribersResponse | null> => {
    try {
      const response = await topicApi.listSubscribers(topicId);
      setSubscriberIds(response.subscriberIds);
      return response;
    } catch (err) {
      handleError(err);
      return null;
    }
  };

  const getPublishStatus = async (publishId: string): Promise<PublishStatusResponse | null> => {
    try {
      return await topicApi.getPublishStatus(topicId, publishId);
    } catch (err) {
      handleError(err);
      return null;
    }
  };

  const resetCircuitBreaker = async (subscriberId: string) => {
    try {
      await topicApi.resetCircuitBreaker(topicId, subscriberId);
    } catch (err) {
      handleError(err);
    }
  };

  const getCircuitBreakerStatus = async (subscriberId: string): Promise<CircuitBreakerStatusResponse | null> => {
    try {
      return await topicApi.getCircuitBreakerStatus(topicId, subscriberId);
    } catch (err) {
      handleError(err);
      return null;
    }
  };

  return {
    currentPayload,
    isPublishing,
    isSubscribing,
    subscriberIds,
    lastPublish,
    error,
    publish,
    subscribe,
    unsubscribe,
    refreshSubscribers,
    getPublishStatus,
    resetCircuitBreaker,
    getCircuitBreakerStatus,
    clearError: () => setError(null),
  };
};
