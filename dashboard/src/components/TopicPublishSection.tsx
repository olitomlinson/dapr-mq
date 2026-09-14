import { useState, useEffect } from 'react';
import type { QueuePayload } from '../types/queue';
import { validateQueuePayload } from '../utils/payloadValidation';
import styles from './PushSection.module.css';

const API_BASE = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8002';

interface TopicPublishSectionProps {
  topicId: string;
  currentPayload: QueuePayload;
  isPublishing: boolean;
  onPublish: (priority: number, payload: QueuePayload, idempotencyKey?: string) => void;
}

export const TopicPublishSection = ({ topicId, currentPayload, isPublishing, onPublish }: TopicPublishSectionProps) => {
  const [activeTab, setActiveTab] = useState<'simple' | 'curl'>('simple');
  const [priority, setPriority] = useState<number>(0);
  const [jsonText, setJsonText] = useState<string>('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [isValid, setIsValid] = useState(true);
  const [idempotencyKey, setIdempotencyKey] = useState<string>('');

  useEffect(() => {
    setJsonText(JSON.stringify(currentPayload, null, 2));
    setValidationError(null);
    setIsValid(true);
  }, [currentPayload]);

  const generateCurl = (topicId: string, payload: QueuePayload, priority: number): string => {
    const item: Record<string, unknown> = { item: payload, priority };
    if (idempotencyKey) item.idempotencyKey = idempotencyKey;
    const body = JSON.stringify({ items: [item] });
    return `curl -X POST '${API_BASE}/topic/${topicId}/publish' \\\n  -H 'Content-Type: application/json' \\\n  -d '${body}'`;
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

  const handlePublish = () => {
    const result = validateQueuePayload(jsonText);
    if (result.valid && result.payload) {
      onPublish(priority, result.payload as unknown as QueuePayload, idempotencyKey || undefined);
    }
  };

  const generateIdempotencyKey = () => setIdempotencyKey(crypto.randomUUID());

  const getPayloadForCurl = (): QueuePayload => {
    const result = validateQueuePayload(jsonText);
    return result.valid && result.payload ? (result.payload as unknown as QueuePayload) : currentPayload;
  };

  return (
    <div className="card">
      <h3>Publish</h3>

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
            disabled={isPublishing}
            spellCheck={false}
            rows={8}
          />
          {validationError && (
            <div className={styles.validationError}>{validationError}</div>
          )}

          <div className={styles.priorityControl}>
            <label>Priority:</label>
            <button onClick={decrementPriority} disabled={isPublishing}>-</button>
            <input
              type="number"
              value={priority}
              onChange={handlePriorityInput}
              min="0"
              disabled={isPublishing}
            />
            <button onClick={incrementPriority} disabled={isPublishing}>+</button>
          </div>

          <div className={styles.idempotencyControl}>
            <label>Idempotency Key:</label>
            <input
              type="text"
              value={idempotencyKey}
              onChange={(e) => setIdempotencyKey(e.target.value)}
              placeholder="optional - per-subscriber dedup within TTL window"
              disabled={isPublishing}
            />
            <button onClick={generateIdempotencyKey} disabled={isPublishing} title="Generate a UUID">
              Generate
            </button>
            {idempotencyKey && (
              <button onClick={() => setIdempotencyKey('')} disabled={isPublishing} title="Clear">
                ×
              </button>
            )}
          </div>

          <button
            className={styles.pushBtn}
            onClick={handlePublish}
            disabled={isPublishing || !isValid}
          >
            {isPublishing ? 'Publishing...' : 'Publish'}
          </button>
        </div>
      )}

      {activeTab === 'curl' && (
        <div className={styles.curlSection}>
          <pre>{generateCurl(topicId, getPayloadForCurl(), priority)}</pre>
          <button
            className={styles.copyBtn}
            onClick={() => copyToClipboard(generateCurl(topicId, getPayloadForCurl(), priority))}
          >
            Copy
          </button>
        </div>
      )}
    </div>
  );
};
