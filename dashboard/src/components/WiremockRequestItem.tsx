import type { WiremockRequest } from '../types/queue';
import { PriorityBadge } from './PriorityBadge';
import { LockInfo } from './LockInfo';
import { API_BASE } from '../services/queueApi';
import { isObjectClaim, withTruncatedClaimToken, truncateClaimTokensDeep } from '../utils/queueHelpers';
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
      return JSON.stringify(truncateClaimTokensDeep(parsed), null, 2);
    } catch {
      return body;
    }
  };

  // Show each item with lock/download controls whenever the request body parsed into items
  // (HTTP sink scenario) - regardless of response status (200 auto-acknowledges, 202 defers ack,
  // but both deliver the same item shape and both may include a downloadable object claim).
  const hasParsedItems = request.parsedItems && request.parsedItems.length > 0;

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

      {/* When the request body parsed into items, show each item with lock/download controls */}
      {hasParsedItems ? (
        request.parsedItems!.map((parsedItem, index) => {
          // Merge lockStates with parsedItem data
          const lockState = parsedItem.lockId ? lockStates?.[parsedItem.lockId] : undefined;
          const claim = isObjectClaim(parsedItem.item) ? parsedItem.item : null;
          const downloadUrl = claim ? `${API_BASE}/object/${claim.objectClaimToken}` : undefined;
          // A 200 OK response auto-acknowledges every delivered item server-side (see
          // HttpSinkActor), so its lock is already gone - render as already-acknowledged rather
          // than offering Acknowledge/Dead Letter buttons that would fail against a stale lock.
          const isAutoAcknowledged = request.response.status === 200;

          return (
            <div key={index} style={{ marginTop: index > 0 ? '12px' : '8px' }}>
              {parsedItem.priority !== undefined && <PriorityBadge priority={parsedItem.priority} />}
              <pre className={styles.requestBody}>
                {JSON.stringify(withTruncatedClaimToken(parsedItem.item), null, 2)}
              </pre>
              {parsedItem.lockId && (
                <LockInfo
                  message={{
                    item: parsedItem.item,
                    priority: parsedItem.priority,
                    locked: true,
                    lockId: parsedItem.lockId,
                    lockExpiresAt: parsedItem.lockExpiresAt,
                    acknowledged: isAutoAcknowledged || lockState?.acknowledged,
                    deadLettered: lockState?.deadLettered,
                    dlqId: lockState?.dlqId,
                  }}
                  onAcknowledge={() => onAcknowledge?.(parsedItem.lockId!)}
                  onDeadLetter={() => onDeadLetter?.(parsedItem.lockId!)}
                  downloadUrl={downloadUrl}
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
