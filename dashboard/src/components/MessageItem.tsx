import type { PoppedMessage } from '../types/queue';
import { PriorityBadge } from './PriorityBadge';
import { LockInfo } from './LockInfo';
import styles from './MessageItem.module.css';

interface MessageItemProps {
  message: PoppedMessage;
  onAcknowledge: () => void;
  onDeadLetter: () => void;
}

export const MessageItem = ({ message, onAcknowledge, onDeadLetter }: MessageItemProps) => {
  const hasLockInfo = message.locked && message.lockId;

  return (
    <div className={styles.container}>
      <PriorityBadge priority={message.priority} />
      <pre className={hasLockInfo ? styles.preWithLock : ''}>
        {JSON.stringify(message.item, null, 2)}
      </pre>
      {hasLockInfo && (
        <LockInfo
          message={message}
          onAcknowledge={onAcknowledge}
          onDeadLetter={onDeadLetter}
        />
      )}
    </div>
  );
};
