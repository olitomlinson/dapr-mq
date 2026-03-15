import { useState, useEffect } from 'react';
import { queueApi, QueueApiError, createApiError } from '../services/queueApi';
import { generatePayload } from '../utils/queueHelpers';
import type { PoppedMessage, ApiError, QueuePayload } from '../types/queue';

export const useQueueOperations = (queueId: string) => {
  const [currentPayload, setCurrentPayload] = useState<QueuePayload>(() => generatePayload());
  const [messagesPushed, setMessagesPushed] = useState(0);
  const [messagesPopped, setMessagesPopped] = useState(0);
  const [poppedMessages, setPoppedMessages] = useState<PoppedMessage[]>([]);
  const [isPushing, setIsPushing] = useState(false);
  const [isPopping, setIsPopping] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  // Reset state when queue ID changes
  useEffect(() => {
    setMessagesPushed(0);
    setMessagesPopped(0);
    setPoppedMessages([]);
    setCurrentPayload(generatePayload());
    setError(null);
  }, [queueId]);

  const pushMessage = async (priority: number) => {
    setIsPushing(true);
    try {
      await queueApi.push(queueId, { items: [{ item: currentPayload, priority }] });
      setMessagesPushed(prev => prev + 1);
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

  const popMessage = async () => {
    setIsPopping(true);
    try {
      const data = await queueApi.pop(queueId);
      if (data === null) {
        alert('Queue is empty');
      } else {
        setMessagesPopped(prev => prev + 1);
        setPoppedMessages(prev => [{
          item: data.item,
          priority: data.priority
        }, ...prev]);
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

  const popWithAck = async () => {
    setIsPopping(true);
    try {
      const data = await queueApi.popWithAck(queueId);
      if (data === null) {
        alert('Queue is empty');
      } else {
        setMessagesPopped(prev => prev + 1);
        setPoppedMessages(prev => [{
          item: data.item,
          priority: data.priority,
          locked: data.locked,
          lockId: data.lockId,
          lockExpiresAt: data.lockExpiresAt,
        }, ...prev]);
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
      const dlqName = data.dlqActorId || `${queueId}-deadletter`;
      setPoppedMessages(prev => prev.map((msg, i) =>
        i === index ? { ...msg, deadLettered: true, dlqActorId: dlqName } : msg
      ));
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
    pushMessage,
    popMessage,
    popWithAck,
    acknowledgeMessage,
    deadLetterMessage,
    clearError: () => setError(null),
  };
};
