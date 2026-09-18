import { getPriorityClassName } from '../utils/queueHelpers';
import styles from './PriorityBadge.module.css';

interface PriorityBadgeProps {
  priority?: number;
}

export const PriorityBadge = ({ priority }: PriorityBadgeProps) => {
  const className = getPriorityClassName(priority);

  return (
    <div className={`${styles.badge} ${styles[className]}`}>
      P{priority ?? '?'}
    </div>
  );
};
