import { useState } from 'react';
import { isDeadLetterQueue, validateQueueId, updateQueueIdInUrl } from '../utils/queueHelpers';
import styles from './QueueHeader.module.css';

interface QueueHeaderProps {
  queueId: string;
  messagesPushed: number;
  messagesPopped: number;
  onQueueIdChange: (newQueueId: string) => void;
  sinkRegistered: boolean;
  sinkUrl?: string;
  onRegisterSink: () => void;
  onUpdateSink: () => void;
  onUnregisterSink: () => void;
  isWiremockDetected?: boolean;
}

export const QueueHeader = ({
  queueId,
  messagesPushed,
  messagesPopped,
  onQueueIdChange,
  sinkRegistered,
  sinkUrl,
  onRegisterSink,
  onUpdateSink,
  onUnregisterSink,
  isWiremockDetected
}: QueueHeaderProps) => {
  const isDLQ = isDeadLetterQueue(queueId);
  const [isEditing, setIsEditing] = useState(false);
  const [editValue, setEditValue] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);

  const handleEditClick = () => {
    setEditValue(queueId);
    setIsEditing(true);
    setValidationError(null);
  };

  const handleSave = () => {
    const validation = validateQueueId(editValue);
    if (!validation.valid) {
      setValidationError(validation.error || 'Invalid queue ID');
      return;
    }

    if (editValue !== queueId) {
      onQueueIdChange(editValue);
      updateQueueIdInUrl(editValue);
    }

    setIsEditing(false);
    setValidationError(null);
  };

  const handleCancel = () => {
    setIsEditing(false);
    setEditValue('');
    setValidationError(null);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') handleSave();
    if (e.key === 'Escape') handleCancel();
  };

  return (
    <div className="card">
      <h3>
        Queue:{' '}
        {isEditing ? (
          <>
            <input
              type="text"
              value={editValue}
              onChange={(e) => setEditValue(e.target.value)}
              onKeyDown={handleKeyDown}
              className={styles.queueInput}
              autoFocus
            />
            <button onClick={handleSave} className={styles.saveButton}>✓</button>
            <button onClick={handleCancel} className={styles.cancelButton}>✗</button>
            {validationError && (
              <span className={styles.validationError}>{validationError}</span>
            )}
          </>
        ) : (
          <>
            <span>{queueId}</span>
            <button onClick={handleEditClick} className={styles.editButton}>✏️</button>
            {!sinkRegistered && (
              <button onClick={onRegisterSink} className={styles.registerSinkButton}>
                Register HTTP Sink
              </button>
            )}
          </>
        )}
        {isDLQ && (
          <span className={styles.deadletterBadge}>☠️ DEAD LETTER</span>
        )}
      </h3>
      {sinkRegistered && sinkUrl && (
        <p className={styles.sinkStatus}>
          <span className={styles.sinkBadge}>
            ⚡ HTTP Sink Active: <code>{sinkUrl}</code>
            <button
              className={styles.updateButton}
              onClick={onUpdateSink}
              title="Update sink configuration"
            >
              configure
            </button>
            <button
              className={styles.unregisterButton}
              onClick={onUnregisterSink}
              title="Unregister sink"
            >
              ×
            </button>
          </span>
          {isWiremockDetected && (
            <span className={styles.wiremockBadge}>
              🧪 using WireMock
            </span>
          )}
        </p>
      )}
      <p>Stats: <span>{messagesPushed} pushed | {messagesPopped} popped</span></p>
    </div>
  );
};
