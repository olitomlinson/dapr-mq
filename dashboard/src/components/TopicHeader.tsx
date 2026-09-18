import { useState } from 'react';
import { validateTopicId } from '../utils/topicHelpers';
import type { PublishResponse } from '../types/topic';
import styles from './QueueHeader.module.css';

interface TopicHeaderProps {
  topicId: string;
  subscriberCount: number;
  lastPublish: PublishResponse | null;
  onTopicIdChange: (newTopicId: string) => void;
}

export const TopicHeader = ({ topicId, subscriberCount, lastPublish, onTopicIdChange }: TopicHeaderProps) => {
  const [isEditing, setIsEditing] = useState(false);
  const [editValue, setEditValue] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);

  const handleEditClick = () => {
    setEditValue(topicId);
    setIsEditing(true);
    setValidationError(null);
  };

  const handleSave = () => {
    const validation = validateTopicId(editValue);
    if (!validation.valid) {
      setValidationError(validation.error || 'Invalid topic ID');
      return;
    }

    if (editValue !== topicId) {
      onTopicIdChange(editValue);
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
        Topic:{' '}
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
            <span>{topicId}</span>
            <button onClick={handleEditClick} className={styles.editButton}>✏️</button>
          </>
        )}
      </h3>
      <p>Stats: <span>{subscriberCount} subscriber{subscriberCount === 1 ? '' : 's'}</span></p>
      {lastPublish && (
        <p style={{ fontSize: '0.9em', color: '#666' }}>
          Last published: <code>{lastPublish.publishId}</code> (sequence {lastPublish.sequence})
        </p>
      )}
    </div>
  );
};
