namespace DaprMQ.Interfaces;

/// <summary>
/// Worker-local, in-process tracking of whether each hosted session has work. Session actors call
/// it on every method; the reporter sends only lasting changes to each SessionCoordinatorActor, in
/// the background, so observing never adds a cross-actor call to the caller's turn.
/// </summary>
public interface ISessionActivityReporter
{
    /// <summary>
    /// Records a session's current has-work state. <paramref name="deactivating"/> marks the
    /// session's final state before this worker stops hosting it.
    /// </summary>
    void Observe(string queueId, string sessionId, bool hasWork, bool deactivating = false);

    /// <summary>
    /// Records that the coordinator already knows this state (sent directly by RegisterSession), so
    /// the reporter doesn't send it again.
    /// </summary>
    void MarkReported(string queueId, string sessionId, bool hasWork);

    /// <summary>
    /// Forgets what the coordinator was last told about this session, so the reporter sends its
    /// current state on the next tick. For a change the coordinator made without telling this worker,
    /// such as a lease expiry.
    /// </summary>
    void ResetReported(string queueId, string sessionId);
}
