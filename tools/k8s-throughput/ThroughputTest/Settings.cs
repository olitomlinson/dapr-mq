using System.Collections;
using System.Globalization;

namespace ThroughputTest;

/// <summary>
/// One run's settings, from environment variables (the chart sets them on the Job and the consumer
/// Deployment alike). The defaults are the run that drained fastest in the KEDA load tests: 20
/// replicas x 10 handlers, a window of 40, 200ms of work per message, a 60s lock.
///
/// The `sessions` workload spreads the same backlog over session queues instead, consumed by a
/// SessionQueueConsumer: by default 1,000 sessions of 100, 10 held at a time per consumer, which is
/// the queue's 10 handlers per consumer.
/// </summary>
public sealed record Settings
{
    public required string QueueId { get; init; }
    /// <summary>queue: competing consumers of one queue. sessions: session consumers of many sessions.</summary>
    public string Workload { get; init; } = "queue";
    public string GatewayHttpUrl { get; init; } = "http://daprmq-gateway:8080";
    public string GatewayGrpcAddress { get; init; } = "http://daprmq-gateway:8081";
    /// <summary>The DaprMQ operator's KEDA external scaler: the run reads depth exactly as KEDA does.</summary>
    public string ScalerAddress { get; init; } = "http://daprmq-operator:8081";
    /// <summary>
    /// The consumers' ScaledObject. The scaler records which ScaledObject asks for a queue's depth, so
    /// the run asks as that one rather than as a name the operator would then report as a scaler.
    /// </summary>
    public string ScaledObjectName { get; init; } = "throughput-consumer";
    public string ScaledObjectNamespace { get; init; } = "";

    // The run (Job).
    public int Messages { get; init; } = 100_000;
    public int Publishers { get; init; } = 10;
    public int PublishBatch { get; init; } = 100;
    public int TimeoutSeconds { get; init; } = 1800;
    public int PollMs { get; init; } = 1000;

    // The consumers (Deployment).
    public int Handlers { get; init; } = 10;
    public int Window { get; init; } = 40;
    public int ItemDelayMs { get; init; } = 200;
    public int LockTtlSeconds { get; init; } = 60;

    // The session consumers (sessions workload). ItemDelayMs is the work per message for both.
    public int Sessions { get; init; } = 1000;
    public int MessagesPerSession => Messages / Sessions;
    /// <summary>SessionQueueConsumerOptions.MaxConcurrentSessions: sessions each consumer holds at once.</summary>
    public int ConcurrentSessions { get; init; } = 10;
    public int Prefetch { get; init; } = 1;
    public int LeaseSeconds { get; init; } = 30;
    /// <summary>
    /// How long a slot waits on its emptied session before claiming another. The server's default is the
    /// lease, which here would hold each drained session for 30s.
    /// </summary>
    public int SessionIdleTimeoutSeconds { get; init; } = 1;

    /// <summary>Most consumer replicas (keda), or exactly this many (fixed).</summary>
    public int Replicas { get; init; } = 20;
    public string ScalingMode { get; init; } = "keda";

    // Where the run happened, for the shared result record (sdks/testing/perf/result.schema.json).
    public string EnvLabel { get; init; } = "local";
    public int GatewayReplicas { get; init; } = 1;
    public string ServerLabel { get; init; } = "kubernetes";
    public string? GitSha { get; init; }
    public string? GitBranch { get; init; }
    public bool GitDirty { get; init; }

    public static Settings FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty));

    public static Settings Parse(IReadOnlyDictionary<string, string> env)
    {
        var defaults = new Settings { QueueId = "unset" };
        string Text(string key, string fallback) => env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        // Present means set, 0 included; only a missing or empty variable takes the default.
        int Int(string key, int fallback, int min)
        {
            if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min)
            {
                throw new ArgumentException($"{key} must be an integer of at least {min}, got '{raw}'");
            }
            return value;
        }

        var queueId = Text("QUEUE_ID", "");
        if (queueId.Length == 0)
        {
            throw new ArgumentException("QUEUE_ID is required");
        }

        var workload = Text("WORKLOAD", defaults.Workload);
        if (workload is not ("queue" or "sessions"))
        {
            throw new ArgumentException($"WORKLOAD must be queue or sessions, got '{workload}'");
        }

        var messages = Int("MESSAGES", defaults.Messages, 1);
        var sessions = Int("SESSIONS", defaults.Sessions, 1);
        if (workload == "sessions" && messages % sessions != 0)
        {
            throw new ArgumentException($"SESSIONS ({sessions}) must divide MESSAGES ({messages}) evenly");
        }

        return new Settings
        {
            QueueId = queueId,
            Workload = workload,
            GatewayHttpUrl = Text("DAPRMQ_HTTP_BASE_URL", defaults.GatewayHttpUrl),
            GatewayGrpcAddress = Text("DAPRMQ_GRPC_ADDRESS", defaults.GatewayGrpcAddress),
            ScalerAddress = Text("SCALER_ADDRESS", defaults.ScalerAddress),
            ScaledObjectName = Text("SCALED_OBJECT_NAME", defaults.ScaledObjectName),
            ScaledObjectNamespace = Text("POD_NAMESPACE", defaults.ScaledObjectNamespace),
            Messages = messages,
            Publishers = Int("PUBLISHERS", defaults.Publishers, 1),
            PublishBatch = Int("PUBLISH_BATCH", defaults.PublishBatch, 1),
            TimeoutSeconds = Int("TIMEOUT_SECONDS", defaults.TimeoutSeconds, 1),
            PollMs = Int("POLL_MS", defaults.PollMs, 100),
            Handlers = Int("HANDLERS", defaults.Handlers, 1),
            Window = Int("WINDOW", defaults.Window, 1),
            ItemDelayMs = Int("ITEM_DELAY_MS", defaults.ItemDelayMs, 0),
            LockTtlSeconds = Int("LOCK_TTL_SECONDS", defaults.LockTtlSeconds, 1),
            Sessions = sessions,
            ConcurrentSessions = Int("CONCURRENT_SESSIONS", defaults.ConcurrentSessions, 1),
            Prefetch = Int("PREFETCH", defaults.Prefetch, 1),
            LeaseSeconds = Int("LEASE_SECONDS", defaults.LeaseSeconds, 1),
            SessionIdleTimeoutSeconds = Int("SESSION_IDLE_TIMEOUT_SECONDS", defaults.SessionIdleTimeoutSeconds, 0),
            Replicas = Int("REPLICAS", defaults.Replicas, 1),
            ScalingMode = Text("SCALING_MODE", defaults.ScalingMode),
            EnvLabel = Text("ENV_LABEL", defaults.EnvLabel),
            GatewayReplicas = Int("GATEWAY_REPLICAS", defaults.GatewayReplicas, 1),
            ServerLabel = Text("SERVER_LABEL", defaults.ServerLabel),
            GitSha = env.TryGetValue("GIT_SHA", out var sha) && sha.Length > 0 ? sha : null,
            GitBranch = env.TryGetValue("GIT_BRANCH", out var branch) && branch.Length > 0 ? branch : null,
            GitDirty = env.TryGetValue("GIT_DIRTY", out var dirty) && dirty == "true"
        };
    }
}
