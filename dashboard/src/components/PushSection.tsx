import { useState, useEffect } from 'react';
import type { QueuePayload } from '../types/queue';
import { validateQueuePayload } from '../utils/payloadValidation';
import styles from './PushSection.module.css';

const API_BASE = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8002';

interface PushSectionProps {
  queueId: string;
  currentPayload: QueuePayload;
  isPushing: boolean;
  onPush: (priority: number, payload: QueuePayload) => void;
}

export const PushSection = ({ queueId, currentPayload, isPushing, onPush }: PushSectionProps) => {
  const [activeTab, setActiveTab] = useState<'simple' | 'curl'>('simple');
  const [priority, setPriority] = useState<number>(0);
  const [jsonText, setJsonText] = useState<string>('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [isValid, setIsValid] = useState(true);

  useEffect(() => {
    setJsonText(JSON.stringify(currentPayload, null, 2));
    setValidationError(null);
    setIsValid(true);
  }, [currentPayload]);

  const generateCurl = (queueId: string, payload: QueuePayload, priority: number): string => {
    const body = JSON.stringify({ items: [{ item: payload, priority }] });
    return `curl -X POST '${API_BASE}/queue/${queueId}/push' \\\n  -H 'Content-Type: application/json' \\\n  -d '${body}'`;
  };

  const copyToClipboard = async (text: string) => {
    await navigator.clipboard.writeText(text);
  };

  const incrementPriority = () => setPriority(p => p + 1);
  const decrementPriority = () => setPriority(p => Math.max(0, p - 1));
  const handlePriorityInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    const value = parseInt(e.target.value, 10);
    if (!isNaN(value) && value >= 0) {
      setPriority(value);
    }
  };

  const handleJsonChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const text = e.target.value;
    setJsonText(text);

    const result = validateQueuePayload(text);
    setIsValid(result.valid);
    setValidationError(result.error || null);
  };

  const handlePush = () => {
    const result = validateQueuePayload(jsonText);
    if (result.valid && result.payload) {
      onPush(priority, result.payload as unknown as QueuePayload);
    }
  };

  const getPayloadForCurl = (): QueuePayload => {
    const result = validateQueuePayload(jsonText);
    return result.valid && result.payload ? (result.payload as unknown as QueuePayload) : currentPayload;
  };

  return (
    <div className="card">
      <h3>Push</h3>

      <div className={styles.tabButtons}>
        <button
          className={activeTab === 'simple' ? styles.tabActive : styles.tab}
          onClick={() => setActiveTab('simple')}
        >
          Simple
        </button>
        <button
          className={activeTab === 'curl' ? styles.tabActive : styles.tab}
          onClick={() => setActiveTab('curl')}
        >
          CURL
        </button>
      </div>

      {activeTab === 'simple' && (
        <div>
          <textarea
            className={styles.jsonEditor}
            value={jsonText}
            onChange={handleJsonChange}
            disabled={isPushing}
            spellCheck={false}
            rows={8}
          />
          {validationError && (
            <div className={styles.validationError}>{validationError}</div>
          )}

          <div className={styles.priorityControl}>
            <label>Priority:</label>
            <button onClick={decrementPriority} disabled={isPushing}>-</button>
            <input
              type="number"
              value={priority}
              onChange={handlePriorityInput}
              min="0"
              disabled={isPushing}
            />
            <button onClick={incrementPriority} disabled={isPushing}>+</button>
          </div>

          <button
            className={styles.pushBtn}
            onClick={handlePush}
            disabled={isPushing || !isValid}
          >
            {isPushing ? 'Pushing...' : 'Push'}
          </button>
        </div>
      )}

      {activeTab === 'curl' && (
        <div className={styles.curlSection}>
          <pre>{generateCurl(queueId, getPayloadForCurl(), priority)}</pre>
          <button
            className={styles.copyBtn}
            onClick={() => copyToClipboard(generateCurl(queueId, getPayloadForCurl(), priority))}
          >
            Copy
          </button>
        </div>
      )}
    </div>
  );
};
