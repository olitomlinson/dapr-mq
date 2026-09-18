import { useState, useEffect } from 'react';
import { queueApi, QueueApiError, createApiError } from '../services/queueApi';
import { generatePayload } from '../utils/queueHelpers';
import type { DequeuedMessage, ApiError, QueuePayload, SinkConfig, RegisterSinkRequest } from '../types/queue';

interface StoredDequeuedState {
  dequeuedMessages: DequeuedMessage[];
  messagesDequeued: number;
}

const dequeuedMessagesStorageKey = (queueId: string) => `daprmq:dequeuedMessages:${queueId}`;

/** Dequeued messages are kept in localStorage per queueId, so switching away and back (e.g.
 * clicking between a topic's subscribers) or reloading the page doesn't lose them - they were
 * already removed from the real queue, so the dashboard is the only place they still exist. */
const loadStoredDequeuedState = (queueId: string): StoredDequeuedState => {
  if (!queueId) return { dequeuedMessages: [], messagesDequeued: 0 };
  try {
    const raw = localStorage.getItem(dequeuedMessagesStorageKey(queueId));
    if (!raw) return { dequeuedMessages: [], messagesDequeued: 0 };
    const parsed = JSON.parse(raw);
    return {
      dequeuedMessages: Array.isArray(parsed.dequeuedMessages) ? parsed.dequeuedMessages : [],
      messagesDequeued: typeof parsed.messagesDequeued === 'number' ? parsed.messagesDequeued : 0,
    };
  } catch {
    return { dequeuedMessages: [], messagesDequeued: 0 };
  }
};

export const useQueueOperations = (queueId: string) => {
  const [currentPayload, setCurrentPayload] = useState<QueuePayload>(() => generatePayload());
  const [messagesEnqueued, setMessagesEnqueued] = useState(0);
  const [messagesDequeued, setMessagesDequeued] = useState(() => loadStoredDequeuedState(queueId).messagesDequeued);
  const [dequeuedMessages, setDequeuedMessages] = useState<DequeuedMessage[]>(() => loadStoredDequeuedState(queueId).dequeuedMessages);
  const [isEnqueuing, setIsEnqueuing] = useState(false);
  const [isDequeuing, setIsDequeuing] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [sinkRegistered, setSinkRegistered] = useState(false);
  const [sinkConfig, setSinkConfig] = useState<SinkConfig | null>(null);
  const [isRegisteringSink, setIsRegisteringSink] = useState(false);
  const [wiremockLockStates, setWiremockLockStates] = useState<Record<string, { acknowledged?: boolean; deadLettered?: boolean; dlqId?: string }>>({});
  const [lastEnqueueDeduplicated, setLastEnqueueDeduplicated] = useState(false);

  // Reset state when queue ID changes - dequeued messages/count are restored from localStorage
  // instead of cleared, so switching queues doesn't lose what was already dequeued from each.
  useEffect(() => {
    const stored = loadStoredDequeuedState(queueId);
    setMessagesEnqueued(0);
    setMessagesDequeued(stored.messagesDequeued);
    setDequeuedMessages(stored.dequeuedMessages);
    setCurrentPayload(generatePayload());
    setError(null);
    setSinkRegistered(false);
    setSinkConfig(null);
    setWiremockLockStates({});
    setLastEnqueueDeduplicated(false);
  }, [queueId]);

  // Persist on every change so acknowledge/dead-letter updates are kept too.
  useEffect(() => {
    if (!queueId) return;
    try {
      localStorage.setItem(dequeuedMessagesStorageKey(queueId), JSON.stringify({ dequeuedMessages, messagesDequeued }));
    } catch {
      // best-effort - private browsing, storage disabled, or quota exceeded are all fine to ignore
    }
  }, [queueId, dequeuedMessages, messagesDequeued]);

  const enqueueMessage = async (priority: number, payload: QueuePayload, idempotencyKey?: string) => {
    setIsEnqueuing(true);
    try {
      const response = await queueApi.enqueue(queueId, {
        items: [{ item: payload, priority, idempotencyKey: idempotencyKey || undefined }],
      });
      const deduplicated = (response.itemsDeduplicated ?? 0) > 0;
      setLastEnqueueDeduplicated(deduplicated);
      if (!deduplicated) {
        setMessagesEnqueued(prev => prev + 1);
      }
      setCurrentPayload(generatePayload());
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    } finally {
      setIsEnqueuing(false);
    }
  };

  const dequeueMessage = async (count: number = 1) => {
    setIsDequeuing(true);
    try {
      const data = await queueApi.dequeue(queueId, count);
      if (data === null) {
        alert('Queue is empty');
      } else {
        const newMessages = data.items.map((responseItem) => ({
          item: responseItem.item,
          priority: responseItem.priority,
          locked: responseItem.lockId ? true : false,
          lockId: responseItem.lockId,
          lockExpiresAt: responseItem.lockExpiresAt,
        }));
        setMessagesDequeued(prev => prev + data.items.length);
        setDequeuedMessages(prev => [...newMessages, ...prev]);
      }
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    } finally {
      setIsDequeuing(false);
    }
  };

  const dequeueLocked = async (count: number = 1, ttl: number = 30, competing: boolean = false) => {
    setIsDequeuing(true);
    try {
      const data = await queueApi.dequeueLocked(queueId, count, ttl, competing);
      if (data === null) {
        alert('Queue is empty');
      } else {
        const newMessages = data.items.map((responseItem) => ({
          item: responseItem.item,
          priority: responseItem.priority,
          locked: true,
          lockId: responseItem.lockId,
          lockExpiresAt: responseItem.lockExpiresAt,
        }));
        setMessagesDequeued(prev => prev + data.items.length);
        setDequeuedMessages(prev => [...newMessages, ...prev]);
      }
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    } finally {
      setIsDequeuing(false);
    }
  };

  const acknowledgeMessage = async (lockId: string, index: number) => {
    try {
      await queueApi.acknowledge(queueId, { lockId });
      setDequeuedMessages(prev => prev.map((msg, i) =>
        i === index ? { ...msg, acknowledged: true } : msg
      ));
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    }
  };

  const deadLetterMessage = async (lockId: string, index: number) => {
    try {
      const data = await queueApi.deadLetter(queueId, { lockId });
      const dlqName = data.dlqId || `${queueId}-deadletter`;
      setDequeuedMessages(prev => prev.map((msg, i) =>
        i === index ? { ...msg, deadLettered: true, dlqId: dlqName } : msg
      ));
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    }
  };

  // Acknowledge without updating local state (for WireMock/HTTP sink items)
  const acknowledgeByLockId = async (lockId: string) => {
    try {
      await queueApi.acknowledge(queueId, { lockId });
      setWiremockLockStates(prev => ({
        ...prev,
        [lockId]: { acknowledged: true }
      }));
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    }
  };

  // Dead letter without updating local state (for WireMock/HTTP sink items)
  const deadLetterByLockId = async (lockId: string) => {
    try {
      const data = await queueApi.deadLetter(queueId, { lockId });
      const dlqName = data.dlqId || `${queueId}-deadletter`;
      setWiremockLockStates(prev => ({
        ...prev,
        [lockId]: { deadLettered: true, dlqId: dlqName }
      }));
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    }
  };

  const registerSink = async (config: RegisterSinkRequest) => {
    setIsRegisteringSink(true);
    try {
      await queueApi.registerSink(queueId, config);
      setSinkRegistered(true);
      setSinkConfig(config);
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
      throw err;
    } finally {
      setIsRegisteringSink(false);
    }
  };

  const unregisterSink = async () => {
    try {
      await queueApi.unregisterSink(queueId);
      setSinkRegistered(false);
      setSinkConfig(null);
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    }
  };

  return {
    currentPayload,
    messagesEnqueued,
    messagesDequeued,
    dequeuedMessages,
    isEnqueuing,
    isDequeuing,
    error,
    lastEnqueueDeduplicated,
    enqueueMessage,
    dequeueMessage,
    dequeueLocked,
    acknowledgeMessage,
    deadLetterMessage,
    acknowledgeByLockId,
    deadLetterByLockId,
    wiremockLockStates,
    clearError: () => setError(null),
    sinkRegistered,
    sinkConfig,
    isRegisteringSink,
    registerSink,
    unregisterSink,
  };
};
