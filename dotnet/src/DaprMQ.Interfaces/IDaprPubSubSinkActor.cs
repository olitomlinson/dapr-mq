using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the Dapr PubSub Sink Actor that delivers messages to Dapr PubSub components using pull-based polling.
/// </summary>
public interface IDaprPubSubSinkActor : IActor
{
    /// <summary>
    /// Initializes the Dapr PubSub sink with configuration and registers a reminder for periodic polling.
    /// </summary>
    /// <param name="request">Configuration including PubSub name, topic, raw payload flag, queue ID, max concurrency, TTL, and polling interval.</param>
    Task InitializeDaprPubSubSink(InitializeDaprPubSubSinkRequest request);

    /// <summary>
    /// Uninitializes the Dapr PubSub sink, unregistering the polling reminder and clearing state.
    /// </summary>
    Task UninitializeDaprPubSubSink();
}
