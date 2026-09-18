using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the SessionCoordinatorActor - tracks which session ids exist for a queue and,
/// once lease mechanics are added, which are currently claimed. One instance per logical queue,
/// addressed by the same id as its QueueActor (a different Dapr actor type, same id string - the
/// two coexist independently). Holds no queue storage itself; per-session message data lives on
/// dedicated QueueActor instances ("{queueId}-session-{sessionId}"), matching the delegation
/// pattern TopicActor already uses for its subscriber queues.
/// </summary>
public interface ISessionCoordinatorActor : IActor
{
    /// <summary>
    /// Called by a per-session QueueActor, once, on its first activation, to announce itself into
    /// this actor's SessionDirectory. Idempotent - safe to call more than once for the same id.
    /// </summary>
    Task<RegisterSessionResponse> RegisterSession(RegisterSessionRequest request);

    /// <summary>
    /// Claim exclusive ownership of a session - "any available" (SessionId omitted) or targeted
    /// (SessionId provided, sticky routing).
    /// </summary>
    Task<AcceptSessionResponse> AcceptSession(AcceptSessionRequest request);

    /// <summary>
    /// Heartbeat to keep an already-claimed lease alive past what would otherwise be its expiry.
    /// </summary>
    Task<RenewSessionLeaseResponse> RenewSessionLease(RenewSessionLeaseRequest request);

    /// <summary>
    /// Explicitly give up a claimed lease, freeing the session immediately.
    /// </summary>
    Task<ReleaseSessionResponse> ReleaseSession(ReleaseSessionRequest request);
}
