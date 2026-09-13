import { useState, useEffect } from 'react';
import { useTopicOperations } from './hooks/useTopicOperations';
import { useQueueOperations } from './hooks/useQueueOperations';
import { TopicHeader } from './components/TopicHeader';
import { TopicPublishSection } from './components/TopicPublishSection';
import { SubscribersPanel } from './components/SubscribersPanel';
import { PopSection } from './components/PopSection';
import { MessageItem } from './components/MessageItem';
import { ErrorModal } from './components/ErrorModal';
import { generateTopicId, updateTopicIdInUrl, buildSubscriberQueueActorId } from './utils/topicHelpers';

function TopicApp() {
  const [topicId, setTopicId] = useState(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get('topic_name') || generateTopicId();
  });
  const [selectedSubscriberId, setSelectedSubscriberId] = useState<string | null>(null);

  const {
    currentPayload,
    isPublishing,
    subscriberIds,
    isSubscribing,
    lastPublish,
    error: topicError,
    publish,
    subscribe,
    unsubscribe,
    refreshSubscribers,
    clearError: clearTopicError,
  } = useTopicOperations(topicId);

  // Subscriber queues are pull-only from the dashboard - pushMessage/isPushing/etc. from this
  // hook are intentionally never wired into the UI below, since new items must only enter via
  // the topic's Publish (reusing PopSection/MessageItem is exactly what's reused; Push is not).
  const subscriberQueueId = selectedSubscriberId ? buildSubscriberQueueActorId(topicId, selectedSubscriberId) : '';
  const {
    poppedMessages,
    isPopping,
    error: queueError,
    popMessage,
    popWithAck,
    acknowledgeMessage,
    deadLetterMessage,
    clearError: clearQueueError,
  } = useQueueOperations(subscriberQueueId);

  useEffect(() => {
    refreshSubscribers();
    setSelectedSubscriberId(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [topicId]);

  const handleTopicIdChange = (newTopicId: string) => {
    setTopicId(newTopicId);
    updateTopicIdInUrl(newTopicId);
  };

  const handleAddSubscriber = async (subscriberId: string) => {
    const result = await subscribe(subscriberId);
    if (result) {
      setSelectedSubscriberId(subscriberId);
    }
  };

  const handleRemoveSubscriber = async (subscriberId: string) => {
    await unsubscribe(subscriberId);
    if (selectedSubscriberId === subscriberId) {
      setSelectedSubscriberId(null);
    }
  };

  return (
    <div className="container">
      <h1>DaprMQ Dashboard — Topics</h1>

      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '2rem', alignItems: 'start' }}>
        {/* Left column */}
        <div>
          <TopicHeader
            topicId={topicId}
            subscriberCount={subscriberIds.length}
            lastPublish={lastPublish}
            onTopicIdChange={handleTopicIdChange}
          />

          <SubscribersPanel
            subscriberIds={subscriberIds}
            isSubscribing={isSubscribing}
            selectedSubscriberId={selectedSubscriberId}
            onAdd={handleAddSubscriber}
            onRemove={handleRemoveSubscriber}
            onSelect={setSelectedSubscriberId}
          />

          {selectedSubscriberId && (
            <div className="card">
              <h3>Popped Messages ({poppedMessages.length}) — {selectedSubscriberId}</h3>
              {poppedMessages.length === 0 ? (
                <p style={{ fontSize: '0.9em', color: '#666', fontStyle: 'italic' }}>
                  No messages popped yet
                </p>
              ) : (
                poppedMessages.map((msg, index) => (
                  <MessageItem
                    key={index}
                    message={msg}
                    onAcknowledge={() => msg.lockId && acknowledgeMessage(msg.lockId, index)}
                    onDeadLetter={() => msg.lockId && deadLetterMessage(msg.lockId, index)}
                  />
                ))
              )}
            </div>
          )}
        </div>

        {/* Right column - sticky controls */}
        <div style={{ position: 'sticky', top: '2rem' }}>
          <TopicPublishSection
            topicId={topicId}
            currentPayload={currentPayload}
            isPublishing={isPublishing}
            onPublish={(priority, payload) => publish(priority, payload)}
          />

          {selectedSubscriberId && (
            <>
              <div className="card">
                <h3>Subscriber Queue: {selectedSubscriberId}</h3>
                <p style={{ fontSize: '0.9em', color: '#666' }}>
                  Queue ID: <code>{subscriberQueueId}</code>
                </p>
                <p style={{ fontSize: '0.9em', color: '#666' }}>
                  Push is disabled here — publish new items via the topic instead.
                </p>
              </div>

              <PopSection
                isPopping={isPopping}
                onPop={(count) => popMessage(count)}
                onPopWithAck={(count, ttl, competing) => popWithAck(count, ttl, competing)}
              />
            </>
          )}
        </div>
      </div>

      <ErrorModal
        error={topicError || queueError}
        onClose={() => { clearTopicError(); clearQueueError(); }}
      />
    </div>
  );
}

export default TopicApp;
