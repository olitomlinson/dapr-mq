import { useQueueId } from './hooks/useQueueId';
import { useQueueOperations } from './hooks/useQueueOperations';
import { QueueHeader } from './components/QueueHeader';
import { PushSection } from './components/PushSection';
import { PopSection } from './components/PopSection';
import { MessagesList } from './components/MessagesList';
import { ErrorModal } from './components/ErrorModal';
import './styles/global.css';

function App() {
  const queueId = useQueueId();
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
    clearError,
  } = useQueueOperations(queueId);

  const showPopSection = messagesPushed > 0;

  return (
    <>
      <div className="container">
        <h1>DaprMQ Dashboard</h1>

        <QueueHeader
          queueId={queueId}
          messagesPushed={messagesPushed}
          messagesPopped={messagesPopped}
        />

        <PushSection
          currentPayload={currentPayload}
          isPushing={isPushing}
          onPush={pushMessage}
        />

        {showPopSection && (
          <PopSection
            isPopping={isPopping}
            onPop={popMessage}
            onPopWithAck={popWithAck}
          />
        )}

        <MessagesList
          messages={poppedMessages}
          onAcknowledge={acknowledgeMessage}
          onDeadLetter={deadLetterMessage}
        />
      </div>

      <ErrorModal error={error} onClose={clearError} />
    </>
  );
}

export default App;
