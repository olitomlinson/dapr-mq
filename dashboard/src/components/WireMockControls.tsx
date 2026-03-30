import { useState, useRef, memo } from 'react';
import { createPortal } from 'react-dom';
import { queueApi } from '../services/queueApi';
import type { ResponseStatus } from '../hooks/useUnifiedWiremock';
import styles from './WireMockControls.module.css';

interface WireMockControlsProps {
  isOpen: boolean;
  onClose: () => void;
  sinkUrl: string;
  queueId: string;
  selectedStatus: ResponseStatus;
  onStatusChange: (status: ResponseStatus) => void;
  blockAutoReapplication: () => void;
  unblockAutoReapplication: () => void;
}

export const WireMockControls = memo(({ isOpen, onClose, sinkUrl, queueId, selectedStatus, onStatusChange, blockAutoReapplication, unblockAutoReapplication }: WireMockControlsProps) => {
  const storageKey = `wiremock-selected-status-${queueId}`;

  const [isApplying, setIsApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const isApplyingRef = useRef(false);

  if (!isOpen) return null;

  const applyMapping = async (status: ResponseStatus) => {
    if (isApplyingRef.current) {
      return;
    }

    isApplyingRef.current = true;
    setIsApplying(true);
    setError(null);

    // Block auto-reapplication in the polling hook to prevent race condition
    blockAutoReapplication();

    try {
      if (status === null) {
        await queueApi.deleteDashboardWiremockMappings(sinkUrl);
      } else {
        await queueApi.resetWiremockMappings(sinkUrl);

        // Small delay to ensure WireMock has processed the reset
        await new Promise(resolve => setTimeout(resolve, 100));

        await queueApi.applyWiremockMapping(sinkUrl, status);
      }
    } catch (err) {
      setError(`Failed to update stub: ${(err as Error).message}`);
    } finally {
      isApplyingRef.current = false;
      setIsApplying(false);
      // Unblock auto-reapplication after manual application completes
      unblockAutoReapplication();
    }
  };

  const handleStatusChange = async (status: ResponseStatus) => {
    localStorage.setItem(storageKey, status === null ? 'null' : status.toString());
    onStatusChange(status);
    await applyMapping(status);
  };

  const handleBackdropClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget) {
      onClose();
    }
  };

  return createPortal(
    <div className={styles.modal} onClick={handleBackdropClick}>
      <div className={styles.content}>
        <div className={styles.header}>
          <div className={styles.title}>Configure WireMock Stub Response</div>
          <button className={styles.close} onClick={onClose} disabled={isApplying}>×</button>
        </div>

        <div className={styles.body}>
          <p className={styles.description}>
            Select how WireMock should respond to HTTP requests from the sink poller.
          </p>

          <div className={styles.radioGroup}>
            <label className={styles.radioLabel}>
              <input
                type="radio"
                name="wiremockStatus"
                value={200}
                checked={selectedStatus === 200}
                onChange={() => handleStatusChange(200)}
                disabled={isApplying}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>
                <span className={styles.statusCode}>200 OK</span>
                <span className={styles.description}>Auto-acknowledge messages</span>
              </span>
            </label>

            <label className={styles.radioLabel}>
              <input
                type="radio"
                name="wiremockStatus"
                value={202}
                checked={selectedStatus === 202}
                onChange={() => handleStatusChange(202)}
                disabled={isApplying}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>
                <span className={styles.statusCode}>202 Accepted</span>
                <span className={styles.description}>Manual acknowledgement required</span>
              </span>
            </label>

            <label className={styles.radioLabel}>
              <input
                type="radio"
                name="wiremockStatus"
                value={500}
                checked={selectedStatus === 500}
                onChange={() => handleStatusChange(500)}
                disabled={isApplying}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>
                <span className={styles.statusCode}>500 Error</span>
                <span className={styles.description}>Simulate failure (locks expire)</span>
              </span>
            </label>

            <label className={styles.radioLabel}>
              <input
                type="radio"
                name="wiremockStatus"
                value="none"
                checked={selectedStatus === null}
                onChange={() => handleStatusChange(null)}
                disabled={isApplying}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>
                <span className={styles.statusCode}>No Configuration</span>
                <span className={styles.description}>Remove dashboard mappings only</span>
              </span>
            </label>
          </div>

          {isApplying && (
            <div className={styles.applying}>Applying configuration...</div>
          )}
          {error && <div className={styles.error}>{error}</div>}
        </div>
      </div>
    </div>,
    document.body
  );
});
