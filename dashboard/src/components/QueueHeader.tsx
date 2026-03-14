import { useState } from 'react';
import { isDeadLetterQueue, validateQueueId, updateQueueIdInUrl } from '../utils/queueHelpers';
import styles from './QueueHeader.module.css';

interface QueueHeaderProps {
  queueId: string;
  messagesPushed: number;
  messagesPopped: number;
  onQueueIdChange: (newQueueId: string) => void;
}

export const QueueHeader = ({ queueId, messagesPushed, messagesPopped, onQueueIdChange }: QueueHeaderProps) => {
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
          </>
        )}
        {isDLQ && (
          <span className={styles.deadletterBadge}>☠️ DEAD LETTER</span>
        )}
      </h3>
      <p>Stats: <span>{messagesPushed} pushed | {messagesPopped} popped</span></p>
    </div>
  );
};
