import { useState } from 'react';
import { useQueueOperations } from './hooks/useQueueOperations';
import { useUnifiedWiremock, type ResponseStatus } from './hooks/useUnifiedWiremock';
import { QueueHeader } from './components/QueueHeader';
import { PushSection } from './components/PushSection';
import { PopSection } from './components/PopSection';
import { MessagesList } from './components/MessagesList';
import { ErrorModal } from './components/ErrorModal';
import { RegisterSinkModal } from './components/RegisterSinkModal';
import { generateQueueId } from './utils/queueHelpers';
import './styles/global.css';

function App() {
  const [queueId, setQueueId] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get('queue_name') || generateQueueId();
  });

  const [showSinkModal, setShowSinkModal] = useState(false);
  const [isEditMode, setIsEditMode] = useState(false);
  const [wiremockSelectedStatus, setWiremockSelectedStatus] = useState<ResponseStatus>(200);

  const {
    currentPayload,
    messagesPushed,
    messagesPopped,
    poppedMessages,
    isPushing,
    isPopping,
    error,
    pushMessage,
    popMessage,
    popWithAck,
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

  const showPopSection = messagesPushed > 0;

  return (
    <>
      <div className="container">
        <h1>DaprMQ Dashboard</h1>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '2rem', alignItems: 'start' }}>
          {/* Left column - Scrollable messages */}
          <div>
            <QueueHeader
              queueId={queueId}
              messagesPushed={messagesPushed}
              messagesPopped={messagesPopped}
              onQueueIdChange={handleQueueIdChange}
              sinkRegistered={sinkRegistered}
              sinkUrl={sinkConfig?.url}
              onRegisterSink={handleRegisterSinkClick}
              onUpdateSink={handleUpdateSinkClick}
              onUnregisterSink={handleUnregisterSink}
              isWiremockDetected={isWiremockDetected}
            />

            <MessagesList
              messages={poppedMessages}
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
            <PushSection
              currentPayload={currentPayload}
              isPushing={isPushing}
              onPush={pushMessage}
            />

            {showPopSection && (
              <PopSection
                isPopping={isPopping}
                onPop={(count) => popMessage(count)}
                onPopWithAck={(count, ttl, competing) => popWithAck(count, ttl, competing)}
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
