import type { PoppedMessage, WiremockRequest } from '../types/queue';
import type { ResponseStatus } from '../hooks/useUnifiedWiremock';
import { MessageItem } from './MessageItem';
import { TabContainer } from './TabContainer';
import { WiremockRequestsList } from './WiremockRequestsList';

interface MessagesListProps {
  messages: PoppedMessage[];
  onAcknowledge: (lockId: string, index: number) => void;
  onDeadLetter: (lockId: string, index: number) => void;
  onAcknowledgeByLockId?: (lockId: string) => void;
  onDeadLetterByLockId?: (lockId: string) => void;
  wiremockLockStates?: Record<string, { acknowledged?: boolean; deadLettered?: boolean; dlqId?: string }>;
  sinkUrl?: string;
  queueId: string;
  isWiremockDetected: boolean;
  wiremockRequests: WiremockRequest[];
  wiremockLoading: boolean;
  wiremockError: string | null;
  wiremockMessageCount: number;
  wiremockSelectedStatus: ResponseStatus;
  onWiremockStatusChange: (status: ResponseStatus) => void;
  blockAutoReapplication: () => void;
  unblockAutoReapplication: () => void;
}

export const MessagesList = ({
  messages,
  onAcknowledge,
  onDeadLetter,
  onAcknowledgeByLockId,
  onDeadLetterByLockId,
  wiremockLockStates,
  sinkUrl,
  queueId,
  isWiremockDetected,
  wiremockRequests,
  wiremockLoading,
  wiremockError,
  wiremockMessageCount,
  wiremockSelectedStatus,
  onWiremockStatusChange,
  blockAutoReapplication,
  unblockAutoReapplication
}: MessagesListProps) => {

  // No sink registered - show simple card
  if (!sinkUrl) {
    return (
      <div className="card">
        <h3>Popped Messages ({messages.length})</h3>
        {messages.length === 0 ? (
          <p style={{ fontSize: '0.9em', color: '#666', fontStyle: 'italic' }}>
            No messages popped yet
          </p>
        ) : (
          messages.map((msg, index) => (
            <MessageItem
              key={index}
              message={msg}
              onAcknowledge={() => msg.lockId && onAcknowledge(msg.lockId, index)}
              onDeadLetter={() => msg.lockId && onDeadLetter(msg.lockId, index)}
            />
          ))
        )}
      </div>
    );
  }

  // Sink registered - always show tabbed interface
  return (
    <div className="card">
      <h3>Messages</h3>
      <TabContainer poppedCount={messages.length} wiremockCount={wiremockMessageCount}>
        {/* Tab 1: Messages */}
        <div>
          {messages.length === 0 ? (
            <p style={{ fontSize: '0.9em', color: '#666', fontStyle: 'italic' }}>
              No messages popped yet
            </p>
          ) : (
            messages.map((msg, index) => (
              <MessageItem
                key={index}
                message={msg}
                onAcknowledge={() => msg.lockId && onAcknowledge(msg.lockId, index)}
                onDeadLetter={() => msg.lockId && onDeadLetter(msg.lockId, index)}
              />
            ))
          )}
        </div>

        {/* Tab 2: WireMock */}
        <WiremockRequestsList
          requests={wiremockRequests}
          isLoading={wiremockLoading}
          error={wiremockError}
          isWiremockDetected={isWiremockDetected}
          onAcknowledge={onAcknowledgeByLockId}
          onDeadLetter={onDeadLetterByLockId}
          lockStates={wiremockLockStates}
          sinkUrl={sinkUrl}
          queueId={queueId}
          selectedStatus={wiremockSelectedStatus}
          onStatusChange={onWiremockStatusChange}
          blockAutoReapplication={blockAutoReapplication}
          unblockAutoReapplication={unblockAutoReapplication}
        />
      </TabContainer>
    </div>
  );
};
