import type { PoppedMessage } from '../types/queue';
import styles from './LockInfo.module.css';

interface LockInfoProps {
  message: PoppedMessage;
  onAcknowledge: () => void;
  onDeadLetter: () => void;
}

export const LockInfo = ({ message, onAcknowledge, onDeadLetter }: LockInfoProps) => {
  const { lockId, lockExpiresAt, acknowledged, deadLettered, dlqId } = message;

  if (!lockId) return null;

  const lockInfoClass = acknowledged
    ? `${styles.lockInfo} ${styles.acknowledged}`
    : deadLettered
    ? `${styles.lockInfo} ${styles.deadlettered}`
    : styles.lockInfo;

  return (
    <div className={lockInfoClass}>
      {acknowledged ? (
        <div>✓ <strong>Message acknowledged successfully</strong></div>
      ) : deadLettered ? (
        <div>
          ✕ <strong>Message moved to dead-letter queue </strong>
          <a
            href={`?queue_name=${dlqId}`}
            target="_blank"
            rel="noopener noreferrer"
            className={styles.dlqLink}
          >
            ({dlqId})
          </a>
        </div>
      ) : (
        <>
          🔒 <strong>Locked</strong> - Requires acknowledgement
          <div className={styles.lockId}>Lock ID: {lockId}</div>
          {lockExpiresAt && (
            <div>Expires: {new Date(lockExpiresAt * 1000).toLocaleString()}</div>
          )}
          <div className={styles.lockActions}>
            <button className={styles.ackBtn} onClick={onAcknowledge}>
              ✓ Acknowledge
            </button>
            <button className={styles.deadletterBtn} onClick={onDeadLetter}>
              ✕ Dead Letter
            </button>
          </div>
        </>
      )}
    </div>
  );
};
