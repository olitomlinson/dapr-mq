import type { WiremockRequest } from '../types/queue';
import { PriorityBadge } from './PriorityBadge';
import { LockInfo } from './LockInfo';
import styles from './WiremockRequestItem.module.css';

interface WiremockRequestItemProps {
  request: WiremockRequest;
  onAcknowledge?: (lockId: string) => void;
  onDeadLetter?: (lockId: string) => void;
  lockStates?: Record<string, { acknowledged?: boolean; deadLettered?: boolean; dlqId?: string }>;
}

export const WiremockRequestItem = ({ request, onAcknowledge, onDeadLetter, lockStates }: WiremockRequestItemProps) => {
  const methodClass = `method${request.request.method.toUpperCase()}`;
  const statusClass = `status${Math.floor(request.response.status / 100)}xx`;

  const formatBody = (body: string): string => {
    try {
      const parsed = JSON.parse(body);
      return JSON.stringify(parsed, null, 2);
    } catch {
      return body;
    }
  };

  // Check if this is a 202 response with parsed items (HTTP sink scenario)
  const is202WithItems = request.response.status === 202 && request.parsedItems && request.parsedItems.length > 0;

  return (
    <div className={styles.requestItem}>
      <div className={styles.requestHeader}>
        <span className={`${styles.methodBadge} ${styles[methodClass]}`}>
          {request.request.method}
        </span>
        <span className={styles.url}>{request.request.url}</span>
        <span className={`${styles.statusBadge} ${styles[statusClass]}`}>
          {request.response.status}
        </span>
        <span className={styles.timestamp}>
          {new Date(request.request.loggedDateString).toLocaleTimeString()}
        </span>
      </div>

      {/* For 202 responses with parsed items, show each item with lock controls */}
      {is202WithItems ? (
        request.parsedItems!.map((parsedItem, index) => {
          // Merge lockStates with parsedItem data
          const lockState = parsedItem.lockId ? lockStates?.[parsedItem.lockId] : undefined;

          return (
            <div key={index} style={{ marginTop: index > 0 ? '12px' : '8px' }}>
              {parsedItem.priority !== undefined && <PriorityBadge priority={parsedItem.priority} />}
              <pre className={styles.requestBody}>
                {JSON.stringify(parsedItem.item, null, 2)}
              </pre>
              {parsedItem.lockId && (
                <LockInfo
                  message={{
                    item: parsedItem.item,
                    priority: parsedItem.priority,
                    locked: true,
                    lockId: parsedItem.lockId,
                    lockExpiresAt: parsedItem.lockExpiresAt,
                    acknowledged: lockState?.acknowledged,
                    deadLettered: lockState?.deadLettered,
                    dlqId: lockState?.dlqId,
                  }}
                  onAcknowledge={() => onAcknowledge?.(parsedItem.lockId!)}
                  onDeadLetter={() => onDeadLetter?.(parsedItem.lockId!)}
                />
              )}
            </div>
          );
        })
      ) : (
        <pre className={styles.requestBody}>
          {formatBody(request.request.body)}
        </pre>
      )}
    </div>
  );
};
