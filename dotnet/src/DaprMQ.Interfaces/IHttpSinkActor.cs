using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the HTTP Sink Actor that delivers messages to external HTTP endpoints using pull-based polling.
/// </summary>
public interface IHttpSinkActor : IActor
{
    /// <summary>
    /// Initializes the HTTP sink with configuration and registers a reminder for periodic polling.
    /// </summary>
    /// <param name="request">Configuration including URL, queue ID, max concurrency, TTL, and polling interval.</param>
    Task InitializeHttpSink(InitializeHttpSinkRequest request);

    /// <summary>
    /// Uninitializes the HTTP sink, unregistering the polling reminder and clearing state.
    /// </summary>
    Task UninitializeHttpSink();
}
