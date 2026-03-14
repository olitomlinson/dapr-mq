import { isDeadLetterQueue } from '../utils/queueHelpers';
import styles from './QueueHeader.module.css';

interface QueueHeaderProps {
  queueId: string;
  messagesPushed: number;
  messagesPopped: number;
}

export const QueueHeader = ({ queueId, messagesPushed, messagesPopped }: QueueHeaderProps) => {
  const isDLQ = isDeadLetterQueue(queueId);

  return (
    <div className="card">
      <h3>
        Queue: <span>{queueId}</span>
        {isDLQ && (
          <span className={styles.deadletterBadge}>☠️ DEAD LETTER</span>
        )}
      </h3>
      <p>Stats: <span>{messagesPushed} pushed | {messagesPopped} popped</span></p>
    </div>
  );
};
