import { useState } from 'react';
import { useQueueOperations } from './hooks/useQueueOperations';
import { useUnifiedWiremock, type ResponseStatus } from './hooks/useUnifiedWiremock';
import { QueueHeader } from './components/QueueHeader';
import { EnqueueSection } from './components/EnqueueSection';
import { DequeueSection } from './components/DequeueSection';
import { MessagesList } from './components/MessagesList';
import { ErrorModal } from './components/ErrorModal';
import { RegisterSinkModal } from './components/RegisterSinkModal';
import TopicApp from './TopicApp';
import { generateQueueId } from './utils/queueHelpers';
import { getInitialMode, updateModeInUrl, type DashboardMode } from './utils/modeHelpers';
import './styles/global.css';

function App() {
  const [mode, setMode] = useState<DashboardMode>(getInitialMode);

  const [queueId, setQueueId] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get('queue_name') || generateQueueId();
  });

  const [showSinkModal, setShowSinkModal] = useState(false);
  const [isEditMode, setIsEditMode] = useState(false);
  const [wiremockSelectedStatus, setWiremockSelectedStatus] = useState<ResponseStatus>(200);

  const {
    currentPayload,
    messagesEnqueued,
    messagesDequeued,
    dequeuedMessages,
    isEnqueuing,
    isDequeuing,
    error,
    lastEnqueueDeduplicated,
    enqueueMessage,
    dequeueMessage,
    dequeueLocked,
    acknowledgeMessage,
    deadLetterMessage,
    acknowledgeByLockId,
    deadLetterByLockId,
    wiremockLockStates,
    clearError,
    sinkRegistered,
    sinkConfig,
    isRegisteringSink,
    registerSink,
    unregisterSink,
  } = useQueueOperations(queueId);

  const {
    isWiremockDetected,
    requests: wiremockRequests,
    isLoading: wiremockLoading,
    error: wiremockError,
    messageCount: wiremockMessageCount,
    blockAutoReapplication,
    unblockAutoReapplication
  } = useUnifiedWiremock(sinkConfig?.url, wiremockSelectedStatus);

  const handleQueueIdChange = (newQueueId: string) => {
    setQueueId(newQueueId);
  };

  const handleModeChange = (newMode: DashboardMode) => {
    setMode(newMode);
    updateModeInUrl(newMode);
  };

  const handleRegisterSinkClick = () => {
    setIsEditMode(false);
    setShowSinkModal(true);
  };

  const handleUpdateSinkClick = () => {
    setIsEditMode(true);
    setShowSinkModal(true);
  };

  const handleUnregisterSink = async () => {
    if (confirm('Are you sure you want to unregister the HTTP sink?')) {
      await unregisterSink();
    }
  };

  const showDequeueSection = messagesEnqueued > 0;

  const modeToggle = (
    <div style={{ maxWidth: '1400px', margin: '0 auto 1rem', display: 'flex', gap: '0.5rem' }}>
      <button onClick={() => handleModeChange('queue')} disabled={mode === 'queue'}>Queue</button>
      <button onClick={() => handleModeChange('topic')} disabled={mode === 'topic'}>Topic</button>
    </div>
  );

  if (mode === 'topic') {
    return (
      <>
        {modeToggle}
        <TopicApp />
      </>
    );
  }

  return (
    <>
      {modeToggle}
      <div className="container">
        <h1>DaprMQ Dashboard</h1>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '2rem', alignItems: 'start' }}>
          {/* Left column - Scrollable messages */}
          <div>
            <QueueHeader
              queueId={queueId}
              messagesEnqueued={messagesEnqueued}
              messagesDequeued={messagesDequeued}
              onQueueIdChange={handleQueueIdChange}
              sinkRegistered={sinkRegistered}
              sinkUrl={sinkConfig?.url}
              onRegisterSink={handleRegisterSinkClick}
              onUpdateSink={handleUpdateSinkClick}
              onUnregisterSink={handleUnregisterSink}
              isWiremockDetected={isWiremockDetected}
            />

            <MessagesList
              messages={dequeuedMessages}
              onAcknowledge={acknowledgeMessage}
              onDeadLetter={deadLetterMessage}
              onAcknowledgeByLockId={acknowledgeByLockId}
              onDeadLetterByLockId={deadLetterByLockId}
              wiremockLockStates={wiremockLockStates}
              sinkUrl={sinkConfig?.url}
              queueId={queueId}
              isWiremockDetected={isWiremockDetected}
              wiremockRequests={wiremockRequests}
              wiremockLoading={wiremockLoading}
              wiremockError={wiremockError}
              wiremockMessageCount={wiremockMessageCount}
              wiremockSelectedStatus={wiremockSelectedStatus}
              onWiremockStatusChange={setWiremockSelectedStatus}
              blockAutoReapplication={blockAutoReapplication}
              unblockAutoReapplication={unblockAutoReapplication}
            />
          </div>

          {/* Right column - Sticky controls */}
          <div style={{ position: 'sticky', top: '2rem' }}>
            <EnqueueSection
              queueId={queueId}
              currentPayload={currentPayload}
              isEnqueuing={isEnqueuing}
              lastEnqueueDeduplicated={lastEnqueueDeduplicated}
              onEnqueue={(priority, payload, idempotencyKey) => enqueueMessage(priority, payload, idempotencyKey)}
            />

            {showDequeueSection && (
              <DequeueSection
                isDequeuing={isDequeuing}
                onDequeue={(count) => dequeueMessage(count)}
                onDequeueLocked={(count, ttl, competing) => dequeueLocked(count, ttl, competing)}
              />
            )}
          </div>
        </div>
      </div>

      <RegisterSinkModal
        isOpen={showSinkModal}
        isRegistering={isRegisteringSink}
        onRegister={registerSink}
        onClose={() => setShowSinkModal(false)}
        initialConfig={isEditMode ? sinkConfig || undefined : undefined}
        isEditMode={isEditMode}
        queueId={queueId}
      />

      <ErrorModal error={error} onClose={clearError} />
    </>
  );
}

export default App;
