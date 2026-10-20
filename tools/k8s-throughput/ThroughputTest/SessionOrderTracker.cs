namespace ThroughputTest;

/// <summary>
/// Checks one consumer's deliveries against each session's FIFO order. A message is a violation when it
/// arrives, for the first time, after a later message of its session; a message seen before is a
/// redelivery (a lapsed lease returns a session's locked messages to its front). It only sees what this
/// consumer handled, so a session that moves between consumers is checked within each one. Thread-safe.
/// </summary>
public sealed class SessionOrderTracker
{
    private readonly Dictionary<string, (long Highest, HashSet<long> Seen)> _sessions = new();
    private readonly object _gate = new();
    private int _fifoViolations;
    private int _redeliveries;

    public int FifoViolations => Volatile.Read(ref _fifoViolations);
    public int Redeliveries => Volatile.Read(ref _redeliveries);

    public void Observe(string sessionId, long seq)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = (seq, new HashSet<long>());
            }

            if (!session.Seen.Add(seq))
            {
                _redeliveries++;
            }
            else if (seq < session.Highest)
            {
                _fifoViolations++;
            }

            _sessions[sessionId] = (Math.Max(session.Highest, seq), session.Seen);
        }
    }
}
