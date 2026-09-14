import { useState } from 'react';
import { validateSubscriberId } from '../utils/topicHelpers';
import styles from './SubscribersPanel.module.css';

interface SubscribersPanelProps {
  subscriberIds: string[];
  isSubscribing: boolean;
  selectedSubscriberId: string | null;
  onAdd: (subscriberId: string, dedupEnabled?: boolean) => void;
  onRemove: (subscriberId: string) => void;
  onSelect: (subscriberId: string) => void;
}

export const SubscribersPanel = ({
  subscriberIds,
  isSubscribing,
  selectedSubscriberId,
  onAdd,
  onRemove,
  onSelect,
}: SubscribersPanelProps) => {
  const [newSubscriberId, setNewSubscriberId] = useState('');
  const [dedupEnabled, setDedupEnabled] = useState(true);
  const [validationError, setValidationError] = useState<string | null>(null);

  const handleAdd = () => {
    const validation = validateSubscriberId(newSubscriberId);
    if (!validation.valid) {
      setValidationError(validation.error || 'Invalid subscriber ID');
      return;
    }
    if (subscriberIds.includes(newSubscriberId)) {
      setValidationError('Subscriber already exists');
      return;
    }

    // Only send an explicit false - true is the queue's own default, no need to set it.
    onAdd(newSubscriberId, dedupEnabled ? undefined : false);
    setNewSubscriberId('');
    setDedupEnabled(true);
    setValidationError(null);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') handleAdd();
  };

  const handleRemove = (subscriberId: string) => {
    if (confirm(`Unsubscribe "${subscriberId}"? Its provisioned queue and any messages already delivered to it are kept.`)) {
      onRemove(subscriberId);
    }
  };

  return (
    <div className="card">
      <h3>Subscribers ({subscriberIds.length})</h3>

      <div className={styles.addRow}>
        <input
          type="text"
          value={newSubscriberId}
          onChange={(e) => setNewSubscriberId(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="subscriber id"
          className={styles.addInput}
          disabled={isSubscribing}
        />
        <button onClick={handleAdd} disabled={isSubscribing || !newSubscriberId}>
          {isSubscribing ? 'Adding...' : 'Add Subscriber'}
        </button>
      </div>
      <label className={styles.dedupToggle}>
        <input
          type="checkbox"
          checked={dedupEnabled}
          onChange={(e) => setDedupEnabled(e.target.checked)}
          disabled={isSubscribing}
        />
        Dedup published items by idempotency key
      </label>
      {validationError && <div className={styles.validationError}>{validationError}</div>}

      {subscriberIds.length === 0 ? (
        <p style={{ fontSize: '0.9em', color: '#666', fontStyle: 'italic' }}>No subscribers yet</p>
      ) : (
        <ul className={styles.list}>
          {subscriberIds.map((subscriberId) => (
            <li
              key={subscriberId}
              className={subscriberId === selectedSubscriberId ? styles.itemSelected : styles.item}
            >
              <button className={styles.selectButton} onClick={() => onSelect(subscriberId)}>
                {subscriberId}
              </button>
              <button
                className={styles.removeButton}
                onClick={() => handleRemove(subscriberId)}
                title="Unsubscribe"
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};
