import { useState } from 'react';
import type { WiremockRequest } from '../types/queue';
import type { ResponseStatus } from '../hooks/useUnifiedWiremock';
import { WiremockRequestItem } from './WiremockRequestItem';
import { WireMockControls } from './WireMockControls';
import styles from './WiremockRequestsList.module.css';

interface WiremockRequestsListProps {
  requests: WiremockRequest[];
  isLoading: boolean;
  error: string | null;
  isWiremockDetected: boolean;
  onAcknowledge?: (lockId: string) => void;
  onDeadLetter?: (lockId: string) => void;
  lockStates?: Record<string, { acknowledged?: boolean; deadLettered?: boolean; dlqId?: string }>;
  sinkUrl?: string;
  queueId: string;
  selectedStatus: ResponseStatus;
  onStatusChange: (status: ResponseStatus) => void;
  blockAutoReapplication: () => void;
  unblockAutoReapplication: () => void;
}

export const WiremockRequestsList = ({
  requests,
  isLoading,
  error,
  isWiremockDetected,
  onAcknowledge,
  onDeadLetter,
  lockStates,
  sinkUrl,
  queueId,
  selectedStatus,
  onStatusChange,
  blockAutoReapplication,
  unblockAutoReapplication
}: WiremockRequestsListProps) => {
  const [isConfigModalOpen, setIsConfigModalOpen] = useState(false);

  if (!isWiremockDetected) {
    return (
      <div className={styles.notDetected}>
        No WireMock detected at this endpoint
      </div>
    );
  }

  if (error) {
    return <div className={styles.error}>Failed to fetch WireMock requests: {error}</div>;
  }

  if (isLoading && requests.length === 0) {
    return <div className={styles.loading}>Connecting to WireMock...</div>;
  }

  const getStatusLabel = (status: ResponseStatus): string => {
    if (status === null) return 'No Configuration';
    switch (status) {
      case 200: return '200 OK';
      case 202: return '202 Accepted';
      case 500: return '500 Error';
    }
  };

  return (
    <>
      {sinkUrl && (
        <div className={styles.configBar}>
          <div className={styles.configInfo}>
            <span className={styles.configLabel}>Stub Response:</span>
            <span className={styles.configValue}>{getStatusLabel(selectedStatus)}</span>
          </div>
          <button
            className={styles.configButton}
            onClick={() => setIsConfigModalOpen(true)}
          >
            Configure
          </button>
        </div>
      )}

      {requests.length === 0 ? (
        <p style={{ fontSize: '0.9em', color: '#666', fontStyle: 'italic' }}>
          No requests received yet
        </p>
      ) : (
        <div className={styles.requestsList}>
          {requests.map(req => (
            <WiremockRequestItem
              key={req.id}
              request={req}
              onAcknowledge={onAcknowledge}
              onDeadLetter={onDeadLetter}
              lockStates={lockStates}
            />
          ))}
        </div>
      )}

      {sinkUrl && (
        <WireMockControls
          isOpen={isConfigModalOpen}
          onClose={() => setIsConfigModalOpen(false)}
          sinkUrl={sinkUrl}
          queueId={queueId}
          selectedStatus={selectedStatus}
          onStatusChange={onStatusChange}
          blockAutoReapplication={blockAutoReapplication}
          unblockAutoReapplication={unblockAutoReapplication}
        />
      )}
    </>
  );
};
