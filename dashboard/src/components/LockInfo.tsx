import type { PoppedMessage } from '../types/queue';
import styles from './LockInfo.module.css';

interface LockInfoProps {
  message: PoppedMessage;
  onAcknowledge: () => void;
  onDeadLetter: () => void;
  downloadUrl?: string;
}

export const LockInfo = ({ message, onAcknowledge, onDeadLetter, downloadUrl }: LockInfoProps) => {
  const { lockId, lockExpiresAt, acknowledged, deadLettered, dlqId } = message;

  if (!lockId) return null;

  const lockInfoClass = acknowledged
    ? `${styles.lockInfo} ${styles.acknowledged}`
    : deadLettered
    ? `${styles.lockInfo} ${styles.deadlettered}`
    : styles.lockInfo;

  const downloadLink = downloadUrl && (
    <a href={downloadUrl} title={downloadUrl} download className={styles.downloadBtn}>
      ⬇ Download
    </a>
  );

  return (
    <div className={lockInfoClass}>
      {acknowledged ? (
        <>
          <div>✓ <strong>Message acknowledged successfully</strong></div>
          {downloadLink && <div className={styles.lockActions}>{downloadLink}</div>}
        </>
      ) : deadLettered ? (
        <>
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
          {downloadLink && <div className={styles.lockActions}>{downloadLink}</div>}
        </>
      ) : (
        <>
          🔒 <strong>Locked</strong> - Requires acknowledgement
          <div className={styles.lockId}>Lock ID: {lockId}</div>
          {lockExpiresAt && (
            <div>Expires: {new Date(lockExpiresAt * 1000).toLocaleString()}</div>
          )}
          <div className={styles.lockActions}>
            {downloadLink}
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
