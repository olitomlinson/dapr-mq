import { useState, useEffect } from 'react';
import { useTopicOperations } from './hooks/useTopicOperations';
import { useQueueOperations } from './hooks/useQueueOperations';
import { TopicHeader } from './components/TopicHeader';
import { TopicPublishSection } from './components/TopicPublishSection';
import { SubscribersPanel } from './components/SubscribersPanel';
import { DequeueSection } from './components/DequeueSection';
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

  // Subscriber queues are pull-only from the dashboard - enqueueMessage/isEnqueuing/etc. from this
  // hook are intentionally never wired into the UI below, since new items must only enter via
  // the topic's Publish (reusing DequeueSection/MessageItem is exactly what's reused; Enqueue is not).
  const subscriberQueueId = selectedSubscriberId ? buildSubscriberQueueActorId(topicId, selectedSubscriberId) : '';
  const {
    dequeuedMessages,
    isDequeuing,
    error: queueError,
    dequeueMessage,
    dequeueLocked,
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

  const handleAddSubscriber = async (subscriberId: string, dedupEnabled?: boolean) => {
    const result = await subscribe(subscriberId, dedupEnabled);
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
              <h3>Dequeued Messages ({dequeuedMessages.length}) — {selectedSubscriberId}</h3>
              {dequeuedMessages.length === 0 ? (
                <p style={{ fontSize: '0.9em', color: '#666', fontStyle: 'italic' }}>
                  No messages dequeued yet
                </p>
              ) : (
                dequeuedMessages.map((msg, index) => (
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
            onPublish={(priority, payload, idempotencyKey) => publish(priority, payload, idempotencyKey)}
          />

          {selectedSubscriberId && (
            <>
              <div className="card">
                <h3>Subscriber Queue: {selectedSubscriberId}</h3>
                <p style={{ fontSize: '0.9em', color: '#666' }}>
                  Queue ID: <code>{subscriberQueueId}</code>
                </p>
                <p style={{ fontSize: '0.9em', color: '#666' }}>
                  Enqueue is disabled here — publish new items via the topic instead.
                </p>
              </div>

              <DequeueSection
                isDequeuing={isDequeuing}
                onDequeue={(count) => dequeueMessage(count)}
                onDequeueLocked={(count, ttl, competing) => dequeueLocked(count, ttl, competing)}
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
