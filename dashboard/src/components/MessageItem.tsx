import type { PoppedMessage } from '../types/queue';
import { PriorityBadge } from './PriorityBadge';
import { LockInfo } from './LockInfo';
import { API_BASE } from '../services/queueApi';
import { isObjectClaim, withTruncatedClaimToken } from '../utils/queueHelpers';
import styles from './MessageItem.module.css';
import lockStyles from './LockInfo.module.css';

interface MessageItemProps {
  message: PoppedMessage;
  onAcknowledge: () => void;
  onDeadLetter: () => void;
}

export const MessageItem = ({ message, onAcknowledge, onDeadLetter }: MessageItemProps) => {
  const hasLockInfo = message.locked && message.lockId;
  const claim = isObjectClaim(message.item) ? message.item : null;
  const downloadUrl = claim ? `${API_BASE}/object/${claim.objectClaimToken}` : undefined;
  const displayItem = withTruncatedClaimToken(message.item);

  return (
    <div className={styles.container}>
      <PriorityBadge priority={message.priority} />
      <pre className={hasLockInfo ? styles.preWithLock : ''}>
        {JSON.stringify(displayItem, null, 2)}
      </pre>
      {claim && !hasLockInfo && (
        <div className={lockStyles.lockActions}>
          <a
            href={downloadUrl}
            title={downloadUrl}
            className={lockStyles.downloadBtn}
            download
          >
            ⬇ Download object
          </a>
        </div>
      )}
      {hasLockInfo && (
        <LockInfo
          message={message}
          onAcknowledge={onAcknowledge}
          onDeadLetter={onDeadLetter}
          downloadUrl={downloadUrl}
        />
      )}
    </div>
  );
};
