import type { QueuePayload } from '../types/queue';
import styles from './PushSection.module.css';

interface PushSectionProps {
  currentPayload: QueuePayload;
  isPushing: boolean;
  onPush: (priority: number) => void;
}

export const PushSection = ({ currentPayload, isPushing, onPush }: PushSectionProps) => {
  return (
    <div className="card">
      <h3>Push a payload</h3>
      <pre>{JSON.stringify(currentPayload, null, 2)}</pre>
      <button
        className={styles.priority0Btn}
        onClick={() => onPush(0)}
        disabled={isPushing}
      >
        {isPushing ? 'Pushing...' : 'Push with Priority 0'}
      </button>
      <button
        onClick={() => onPush(1)}
        disabled={isPushing}
      >
        {isPushing ? 'Pushing...' : 'Push with Priority 1'}
      </button>
    </div>
  );
};
