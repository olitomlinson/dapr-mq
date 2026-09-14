import { useState, useEffect } from 'react';
import { queueApi, QueueApiError, createApiError } from '../services/queueApi';
import { generatePayload } from '../utils/queueHelpers';
import type { PoppedMessage, ApiError, QueuePayload, SinkConfig, RegisterSinkRequest } from '../types/queue';

interface StoredPoppedState {
  poppedMessages: PoppedMessage[];
  messagesPopped: number;
}

const poppedMessagesStorageKey = (queueId: string) => `daprmq:poppedMessages:${queueId}`;

/** Popped messages are kept in localStorage per queueId, so switching away and back (e.g.
 * clicking between a topic's subscribers) or reloading the page doesn't lose them - they were
 * already removed from the real queue, so the dashboard is the only place they still exist. */
const loadStoredPoppedState = (queueId: string): StoredPoppedState => {
  if (!queueId) return { poppedMessages: [], messagesPopped: 0 };
  try {
    const raw = localStorage.getItem(poppedMessagesStorageKey(queueId));
    if (!raw) return { poppedMessages: [], messagesPopped: 0 };
    const parsed = JSON.parse(raw);
    return {
      poppedMessages: Array.isArray(parsed.poppedMessages) ? parsed.poppedMessages : [],
      messagesPopped: typeof parsed.messagesPopped === 'number' ? parsed.messagesPopped : 0,
    };
  } catch {
    return { poppedMessages: [], messagesPopped: 0 };
  }
};

export const useQueueOperations = (queueId: string) => {
  const [currentPayload, setCurrentPayload] = useState<QueuePayload>(() => generatePayload());
  const [messagesPushed, setMessagesPushed] = useState(0);
  const [messagesPopped, setMessagesPopped] = useState(() => loadStoredPoppedState(queueId).messagesPopped);
  const [poppedMessages, setPoppedMessages] = useState<PoppedMessage[]>(() => loadStoredPoppedState(queueId).poppedMessages);
  const [isPushing, setIsPushing] = useState(false);
  const [isPopping, setIsPopping] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [sinkRegistered, setSinkRegistered] = useState(false);
  const [sinkConfig, setSinkConfig] = useState<SinkConfig | null>(null);
  const [isRegisteringSink, setIsRegisteringSink] = useState(false);
  const [wiremockLockStates, setWiremockLockStates] = useState<Record<string, { acknowledged?: boolean; deadLettered?: boolean; dlqId?: string }>>({});
  const [lastPushDeduplicated, setLastPushDeduplicated] = useState(false);

  // Reset state when queue ID changes - popped messages/count are restored from localStorage
  // instead of cleared, so switching queues doesn't lose what was already popped from each.
  useEffect(() => {
    const stored = loadStoredPoppedState(queueId);
    setMessagesPushed(0);
    setMessagesPopped(stored.messagesPopped);
    setPoppedMessages(stored.poppedMessages);
    setCurrentPayload(generatePayload());
    setError(null);
    setSinkRegistered(false);
    setSinkConfig(null);
    setWiremockLockStates({});
    setLastPushDeduplicated(false);
  }, [queueId]);

  // Persist on every change so acknowledge/dead-letter updates are kept too.
  useEffect(() => {
    if (!queueId) return;
    try {
      localStorage.setItem(poppedMessagesStorageKey(queueId), JSON.stringify({ poppedMessages, messagesPopped }));
    } catch {
      // best-effort - private browsing, storage disabled, or quota exceeded are all fine to ignore
    }
  }, [queueId, poppedMessages, messagesPopped]);

  const pushMessage = async (priority: number, payload: QueuePayload, idempotencyKey?: string) => {
    setIsPushing(true);
    try {
      const response = await queueApi.push(queueId, {
        items: [{ item: payload, priority, idempotencyKey: idempotencyKey || undefined }],
      });
      const deduplicated = (response.itemsDeduplicated ?? 0) > 0;
      setLastPushDeduplicated(deduplicated);
      if (!deduplicated) {
        setMessagesPushed(prev => prev + 1);
      }
      setCurrentPayload(generatePayload());
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    } finally {
      setIsPushing(false);
    }
  };

  const popMessage = async (count: number = 1) => {
    setIsPopping(true);
    try {
      const data = await queueApi.pop(queueId, count);
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
        setMessagesPopped(prev => prev + data.items.length);
        setPoppedMessages(prev => [...newMessages, ...prev]);
      }
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    } finally {
      setIsPopping(false);
    }
  };

  const popWithAck = async (count: number = 1, ttl: number = 30, competing: boolean = false) => {
    setIsPopping(true);
    try {
      const data = await queueApi.popWithAck(queueId, count, ttl, competing);
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
        setMessagesPopped(prev => prev + data.items.length);
        setPoppedMessages(prev => [...newMessages, ...prev]);
      }
    } catch (err) {
      if (err instanceof QueueApiError) {
        setError(createApiError(err.status, err.data));
      } else {
        setError(createApiError('Network Error', (err as Error).message));
      }
    } finally {
      setIsPopping(false);
    }
  };

  const acknowledgeMessage = async (lockId: string, index: number) => {
    try {
      await queueApi.acknowledge(queueId, { lockId });
      setPoppedMessages(prev => prev.map((msg, i) =>
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
      setPoppedMessages(prev => prev.map((msg, i) =>
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
    messagesPushed,
    messagesPopped,
    poppedMessages,
    isPushing,
    isPopping,
    error,
    lastPushDeduplicated,
    pushMessage,
    popMessage,
    popWithAck,
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
