namespace DaprMQ.Client.Perf;

public static class Benchmarks
{
    public const string SessionDrain = "session-drain";
    public const string StateReads = "state-reads";
}

public sealed record PerfOptions
{
    public string Benchmark { get; init; } = Benchmarks.SessionDrain;

    public string Profile { get; init; } = "full";
    public int Sessions { get; init; } = 1000;
    public int MessagesPerSession { get; init; } = 100;
    public int SettleMs { get; init; } = 1000;
    public int MaxConcurrentSessions { get; init; } = 20;

    // SDK defaults, so the benchmark measures the out-of-the-box consumer unless told otherwise.
    public int PrefetchCount { get; init; } = 10;
    public int LeaseSeconds { get; init; } = 30;
    public int SessionIdleTimeoutSeconds { get; init; } = 0;

    public string PublishMode { get; init; } = PublishModes.Before;

    /// <summary>0 with no jitter = each session's messages go in one enqueue call.</summary>
    public int PublishIntervalMs { get; init; } = 0;
    public int PublishJitterMs { get; init; } = 0;

    /// <summary>Null = start a throwaway Testcontainers stack.</summary>
    public string? HttpEndpoint { get; init; }
    public string? GrpcEndpoint { get; init; }

    public string EnvLabel { get; init; } = $"local-{Environment.MachineName.ToLowerInvariant()}";
    public string OutDir { get; init; } = DefaultOutDir();
    public bool ReportOnly { get; init; }

    /// <summary>Named set of profiles run back to back against one stack (see <see cref="Suites"/>).</summary>
    public string? Suite { get; init; }

    /// <summary>Exit non-zero when a metric regresses against the baseline branch. Off = report only.</summary>
    public bool Gate { get; init; }
    public string BaselineBranch { get; init; } = "main";

    /// <summary>
    /// Scenario presets. The CI ones use short settle times so server/SDK overhead dominates the
    /// numbers rather than being hidden behind handler time.
    /// </summary>
    private static readonly Dictionary<string, Func<PerfOptions, PerfOptions>> Profiles = new()
    {
        ["full"] = o => o,
        ["quick"] = o => o with { Sessions = 100, MessagesPerSession = 20 },

        // Overall throughput: delivery -> ack -> next delivery, plus moving between sessions.
        ["steady-drain"] = o => o with { Sessions = 200, MessagesPerSession = 20, SettleMs = 100, SessionIdleTimeoutSeconds = 1 },

        // Claim path (AcceptSession, lease, least-recently-claimed ordering) as the hot path.
        ["session-churn"] = o => o with { Sessions = 300, MessagesPerSession = 2, SettleMs = 50, SessionIdleTimeoutSeconds = 1 },

        // Long sessions: segment rollover, prefetch keeping up, lock/ack cost deep into a session.
        ["deep-session"] = o => o with { Sessions = 4, MessagesPerSession = 1000, SettleMs = 10, MaxConcurrentSessions = 4, SessionIdleTimeoutSeconds = 1 },

        // Producer and consumer together: how fast a new message reaches a slot already holding its
        // session. Sessions == slots and a publisher slower than settle, so every session is held from
        // the start and delivery latency is pickup time, not backlog.
        ["live-publish"] = o => o with
        {
            Sessions = 20, MessagesPerSession = 50, SettleMs = 100, SessionIdleTimeoutSeconds = 1,
            PublishMode = PublishModes.Concurrent, PublishIntervalMs = 200, PublishJitterMs = 100,
        },

        // The out-of-the-box consumer, so changes to SDK/server defaults show up.
        ["sdk-defaults"] = o => o with { Sessions = 40, MessagesPerSession = 5 },
    };

    public static readonly IReadOnlyDictionary<string, string[]> Suites = new Dictionary<string, string[]>
    {
        ["ci"] = ["steady-drain", "session-churn", "deep-session", "live-publish", "sdk-defaults"],
    };

    private static readonly HashSet<string> ScenarioFlags =
    [
        "--profile", "--sessions", "--messages", "--settle-ms", "--max-sessions", "--prefetch", "--lease",
        "--idle-timeout", "--publish-mode", "--publish-interval-ms", "--publish-jitter-ms",
    ];

    /// <summary>
    /// The runs this invocation performs: the suite's profiles, or just this one. Parse rejects
    /// scenario flags alongside --suite, so this instance's scenario is still the default one.
    /// </summary>
    public IReadOnlyList<PerfOptions> Runs() => Suite == null
        ? [this]
        : Suites[Suite].Select(profile => WithProfile(this, profile)).ToList();

    private static PerfOptions WithProfile(PerfOptions options, string profile) =>
        Profiles.TryGetValue(profile, out var apply)
            ? apply(options) with { Profile = profile }
            : throw new ArgumentException($"Unknown profile '{profile}'. Known: {string.Join(", ", Profiles.Keys)}.");

    public ScenarioParams Scenario => new(Sessions, MessagesPerSession, SettleMs, MaxConcurrentSessions, PrefetchCount, LeaseSeconds, SessionIdleTimeoutSeconds, PublishMode, PublishIntervalMs, PublishJitterMs);

    public const string Usage = """
        Usage: dotnet run -c Release -- [options]

          --benchmark NAME       session-drain (default) or state-reads: actor state
                                 reads/writes per operation, from the Postgres log
          --profile NAME         full: 1000 sessions x 100 msgs (default); quick: 100 x 20;
                                 CI: steady-drain, session-churn, deep-session,
                                 live-publish, sdk-defaults
          --suite ci             run the five CI profiles against one stack
          --sessions N           sessions to publish
          --messages N           messages per session
          --settle-ms N          handler time per message (default 1000)
          --max-sessions N       SessionQueueConsumer.MaxConcurrentSessions (default 20)
          --prefetch N           PrefetchCount (default 10)
          --lease N              LeaseSeconds (default 30)
          --idle-timeout N       SessionIdleTimeoutSeconds, 0 = server default (= lease)
          --publish-mode M       before: publish everything, then consume (default)
                                 concurrent: start consuming as publishing starts
          --publish-interval-ms N  rate limit: all sessions publish in parallel, one
                                 message every N ms per session (default 0 = one batch)
          --publish-jitter-ms N  add random 0..N ms to each delay (and the start offset)
          --http URL --grpc URL  use an existing server instead of Testcontainers
          --env-label NAME       series name on the charts (default local-<host>)
          --out DIR              results directory (default sdks/dotnet/perf/results)
          --report               only regenerate report.html from existing results
          --baseline-branch B    compare against recent runs from branch B (default main)
          --gate                 exit 3 if a metric regresses (default: report only)
        """;

    public static PerfOptions Parse(string[] args)
    {
        var flags = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "--report" or "--gate")
            {
                flags[name] = null;
                continue;
            }

            if (!name.StartsWith("--") || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Unknown or incomplete option '{name}'.\n\n{Usage}");
            }

            flags[name] = args[++i];
        }

        if (flags.TryGetValue("--benchmark", out var benchmark))
        {
            if (benchmark is not (Benchmarks.SessionDrain or Benchmarks.StateReads))
            {
                throw new ArgumentException($"Unknown benchmark '{benchmark}'. Known: {Benchmarks.SessionDrain}, {Benchmarks.StateReads}.");
            }

            if (benchmark == Benchmarks.StateReads
                && flags.Keys.FirstOrDefault(f => f is "--suite" or "--http" or "--grpc" || ScenarioFlags.Contains(f)) is { } conflict)
            {
                throw new ArgumentException($"{conflict} can't be combined with --benchmark state-reads: it runs fixed steps against its own instrumented stack.");
            }
        }

        if (flags.TryGetValue("--suite", out var suite))
        {
            if (!Suites.ContainsKey(suite!))
            {
                throw new ArgumentException($"Unknown suite '{suite}'. Known: {string.Join(", ", Suites.Keys)}.");
            }

            if (flags.Keys.FirstOrDefault(ScenarioFlags.Contains) is { } conflict)
            {
                throw new ArgumentException($"{conflict} can't be combined with --suite; the suite's profiles fix the scenario.");
            }
        }

        // Profile first, so explicit flags override it regardless of argument order.
        var options = flags.TryGetValue("--profile", out var profile)
            ? WithProfile(new PerfOptions(), profile!)
            : new PerfOptions();

        foreach (var (name, value) in flags)
        {
            options = name switch
            {
                "--profile" => options,
                "--benchmark" => options with { Benchmark = value! },
                "--report" => options with { ReportOnly = true },
                "--suite" => options with { Suite = value },
                "--gate" => options with { Gate = true },
                "--baseline-branch" => options with { BaselineBranch = value! },
                "--sessions" => options with { Sessions = int.Parse(value!) },
                "--messages" => options with { MessagesPerSession = int.Parse(value!) },
                "--settle-ms" => options with { SettleMs = int.Parse(value!) },
                "--max-sessions" => options with { MaxConcurrentSessions = int.Parse(value!) },
                "--prefetch" => options with { PrefetchCount = int.Parse(value!) },
                "--lease" => options with { LeaseSeconds = int.Parse(value!) },
                "--idle-timeout" => options with { SessionIdleTimeoutSeconds = int.Parse(value!) },
                "--publish-mode" => value is PublishModes.Before or PublishModes.Concurrent
                    ? options with { PublishMode = value }
                    : throw new ArgumentException($"Unknown publish mode '{value}'."),
                "--publish-interval-ms" => options with { PublishIntervalMs = NonNegative(name, value!) },
                "--publish-jitter-ms" => options with { PublishJitterMs = NonNegative(name, value!) },
                "--http" => options with { HttpEndpoint = value },
                "--grpc" => options with { GrpcEndpoint = value },
                "--env-label" => options with { EnvLabel = value! },
                "--out" => options with { OutDir = Path.GetFullPath(value!) },
                _ => throw new ArgumentException($"Unknown option '{name}'.\n\n{Usage}")
            };
        }

        if ((options.HttpEndpoint == null) != (options.GrpcEndpoint == null))
        {
            throw new ArgumentException("--http and --grpc must be given together.");
        }

        return options;
    }

    private static int NonNegative(string name, string value) =>
        int.Parse(value) is var n and >= 0 ? n : throw new ArgumentException($"{name} must be >= 0.");

    private static string DefaultOutDir()
    {
        // Walk up from the binary to the project dir so `dotnet run` from anywhere lands in perf/results.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DaprMQ.Client.Perf.csproj")))
            {
                return Path.Combine(dir.Parent!.FullName, "results");
            }
        }

        return Path.GetFullPath("results");
    }
}
