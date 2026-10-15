namespace DaprMQ.Interfaces;

/// <summary>
/// Timings for the session directory: how workers report session changes, and how the
/// SessionCoordinatorActor defers, evicts and backstops entries. See proposals/session-directory.md.
/// </summary>
public class SessionDirectoryConfig
{
    /// <summary>How often a worker's reporter sends changes.</summary>
    public TimeSpan ReportTick { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Upper bound on one report call. Must stay below <see cref="DeferralWindow"/>, which relies on
    /// no report arriving later than this.
    /// </summary>
    public TimeSpan ReportCallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Timeout on a session's direct RegisterSession call, made inside its own turn. A coordinator
    /// turn that is waiting on the same session (SetSessionLease, ClearSessionLease) can't answer it,
    /// so the timeout is what breaks that cycle; the reporter then sends the change on its next tick.
    /// Keep it well above a normal coordinator turn, or new sessions fall back to the reporter.
    /// </summary>
    public TimeSpan RegisterSessionTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a session must stay empty before its reporter says so.</summary>
    public TimeSpan EmptyDebounce { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An Empty change this soon after the session was last marked Active is deferred.</summary>
    public TimeSpan DeferralWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an entry stays Idle before its final eviction read.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>First backstop read of an Active, unleased entry; doubles up to <see cref="MaxBackstop"/>.</summary>
    public TimeSpan Backstop { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan MaxBackstop { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Period of the eviction pass while the directory is non-empty.</summary>
    public TimeSpan EvictionPeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Hard cap on how long one eviction pass holds the coordinator's turn (excluding its one save).</summary>
    public TimeSpan MaxPassDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxReadsPerPass { get; init; } = 64;

    public int ReadParallelism { get; init; } = 16;

    /// <summary>Gap before a catch-up pass while a backlog drains, so other calls get the coordinator in between.</summary>
    public TimeSpan CatchUpDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout on the in-turn lease-end call (ClearSessionLease) or read (lease expiry).</summary>
    public TimeSpan LeaseEndCallTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
