using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the TopicActor - fan-out pub/sub built on top of QueueActor delegation.
/// Publishing makes an item available to every subscriber; each subscriber is backed by its
/// own independent QueueActor instance, so consumers get full FIFO/lock/DLQ semantics for free.
/// IRemindable is implemented directly on TopicActor (not here), matching QueueActor/IQueueActor.
/// </summary>
public interface ITopicActor : IActor
{
    Task<PublishResponse> Publish(PublishRequest request);

    Task<SubscribeResponse> Subscribe(SubscribeRequest request);

    Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request);

    Task<ListSubscribersResponse> ListSubscribers();

    Task<PublishStatusResponse> GetPublishStatus(GetPublishStatusRequest request);

    Task<ResetCircuitBreakerResponse> ResetCircuitBreaker(ResetCircuitBreakerRequest request);

    Task<CircuitBreakerStatusResponse> GetCircuitBreakerStatus(GetCircuitBreakerStatusRequest request);
}
