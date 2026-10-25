namespace DaprMQ.Interfaces;

/// <summary>
/// Worker-local, in-process tracking of whether each hosted session has work. Session actors call
/// it on every method; the reporter sends only lasting changes to each SessionCoordinatorActor, in
/// the background, so observing never adds a cross-actor call to the caller's turn.
/// </summary>
public interface ISessionActivityReporter
{
    /// <summary>
    /// Records a session's current has-work state. A session that has work and hasn't been sent as
    /// Active is sent straight away, outside the caller's turn. <paramref name="deactivating"/> marks
    /// the session's final state before this worker stops hosting it. <paramref name="reported"/>
    /// means the coordinator already knows this state (it decided it, as at a lease end), so nothing
    /// is sent.
    /// </summary>
    void Observe(string queueId, string sessionId, bool hasWork, bool deactivating = false, bool reported = false);

    /// <summary>
    /// Forgets what the coordinator was last told about this session, so the reporter sends its
    /// current state (straight away if it has work, otherwise on the next tick). For a change the
    /// coordinator made without telling this worker, such as a lease expiry.
    /// </summary>
    void ResetReported(string queueId, string sessionId);

    /// <summary>
    /// Whether the coordinator has accepted this session as Active from this worker. While it has,
    /// the coordinator holds an entry for the session, and its eviction pass would find any work in
    /// it, so a session actor needs no registration backstop.
    /// </summary>
    bool IsReportedActive(string queueId, string sessionId);
}
