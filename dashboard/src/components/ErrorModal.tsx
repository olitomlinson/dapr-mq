import type { ApiError } from '../types/queue';
import styles from './ErrorModal.module.css';

interface ErrorModalProps {
  error: ApiError | null;
  onClose: () => void;
}

export const ErrorModal = ({ error, onClose }: ErrorModalProps) => {
  if (!error) return null;

  const handleBackdropClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget) {
      onClose();
    }
  };

  return (
    <div className={styles.modal} onClick={handleBackdropClick}>
      <div className={styles.content}>
        <div className={styles.header}>
          <div className={styles.title}>Error</div>
          <button className={styles.close} onClick={onClose}>×</button>
        </div>
        <div className={styles.body}>
          <div className={styles.status}>Status: {error.status}</div>
          <div className={styles.message}>
            {typeof error.data === 'string'
              ? error.data
              : JSON.stringify(error.data, null, 2)}
          </div>
        </div>
      </div>
    </div>
  );
};
