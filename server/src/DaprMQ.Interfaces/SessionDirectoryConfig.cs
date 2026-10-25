namespace DaprMQ.Interfaces;

/// <summary>
/// Timings for the session directory: how workers report session changes, and how the
/// SessionCoordinatorActor defers, evicts and backstops entries. See proposals/session-directory.md.
/// </summary>
public class SessionDirectoryConfig
{
    /// <summary>
    /// How often a worker's reporter sends Empty changes, and resends anything unsent. A session that
    /// gets work is sent straight away, not on the tick.
    /// </summary>
    public TimeSpan ReportTick { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>First wait before retrying a failed report; doubles up to <see cref="ReportRetryMax"/>.</summary>
    public TimeSpan ReportRetryInitial { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan ReportRetryMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Most changes in one report call, so one coordinator turn stays short.</summary>
    public int ReportBatchMax { get; init; } = 500;

    /// <summary>
    /// When a session actor's one-shot registration backstop fires. Armed before an enqueue saves,
    /// whenever the coordinator hasn't accepted the session as Active, so a session whose report
    /// was lost with its worker is still registered.
    /// </summary>
    public TimeSpan RegistrationBackstop { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on one report call. Must stay below <see cref="DeferralWindow"/>, which relies on
    /// no report arriving later than this.
    /// </summary>
    public TimeSpan ReportCallTimeout { get; init; } = TimeSpan.FromSeconds(5);

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
