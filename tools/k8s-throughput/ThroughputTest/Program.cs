using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DaprMQ.Client;
using Externalscaler;
using Grpc.Net.Client;

namespace ThroughputTest;

/// <summary>
/// The in-cluster throughput test. `consume` is a competing consumer, or a session consumer for the
/// sessions workload (the chart runs it as a KEDA-scaled Deployment); `run` publishes the backlog, waits
/// for the consumers to drain it, and prints one "RESULT {json}" line (the chart runs it as a Job).
/// Settings come from the environment.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Plaintext gRPC (h2c) to the gateway and the operator inside the cluster.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var settings = Settings.FromEnvironment();
        await using var client = new DaprMQClient(new DaprMQClientOptions
        {
            HttpBaseAddress = new Uri(settings.GatewayHttpUrl),
            GrpcAddress = settings.GatewayGrpcAddress
        });

        return args.FirstOrDefault() switch
        {
            "run" => await RunAsync(settings, client),
            "consume" when settings.Workload == "sessions" => await ConsumeSessionsAsync(settings, client),
            "consume" => await ConsumeAsync(settings, client),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: ThroughputTest run|consume (settings from the environment; see Settings.cs)");
        return 64;
    }

    private static void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");

    /// <summary>How often each consumer sends its cumulative stats to the run's stats queue.</summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);

    public static string StatsQueue(Settings settings) => $"{settings.QueueId}-stats";

    private static async Task<int> ConsumeAsync(Settings settings, DaprMQClient client)
    {
        long processed = 0;
        int busy = 0;
        long firstHandledMs = 0;
        var latency = new LatencyHistogram();
        var pod = Environment.GetEnvironmentVariable("HOSTNAME") ?? Guid.NewGuid().ToString("N");
        long seq = 0;
        var options = new QueueConsumerOptions
        {
            MaxConcurrentHandlers = settings.Handlers,
            MaxActiveMessages = settings.Window,
            LockTtl = TimeSpan.FromSeconds(settings.LockTtlSeconds),
            AllowCompetingConsumers = true
        };
        await using var consumer = new QueueConsumer(client, settings.QueueId, options, async (ctx, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.CompareExchange(ref firstHandledMs, now, 0);
            if (ctx.Item.TryGetProperty("publishedAt", out var publishedAt))
            {
                latency.Record(now - publishedAt.GetInt64());
            }
            Interlocked.Increment(ref busy);
            try
            {
                if (settings.ItemDelayMs > 0)
                {
                    await Task.Delay(settings.ItemDelayMs, ct);
                }
                Interlocked.Increment(ref processed);
            }
            finally
            {
                Interlocked.Decrement(ref busy);
            }
        });

        async Task ReportAsync()
        {
            var first = Interlocked.Read(ref firstHandledMs);
            var report = new ConsumerStatsReport(pod, ++seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Interlocked.Read(ref processed), Volatile.Read(ref busy), first == 0 ? null : first, latency.Snapshot());
            try
            {
                await client.EnqueueAsync(StatsQueue(settings), [new EnqueueItemDto(report)]);
            }
            catch (Exception ex)
            {
                Log($"stats report failed: {ex.Message}"); // the next report carries the same totals
            }
        }

        using var stopping = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });

        Log($"consuming {settings.QueueId}: {settings.Handlers} handlers, window {settings.Window}, {settings.ItemDelayMs}ms per message, lock TTL {settings.LockTtlSeconds}s");
        await consumer.StartAsync();
        try
        {
            var nextLog = DateTime.UtcNow;
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(StatsInterval, stopping.Token);
                await ReportAsync();
                if (DateTime.UtcNow >= nextLog)
                {
                    Log($"processed {Interlocked.Read(ref processed)}");
                    nextLog = DateTime.UtcNow.AddSeconds(30);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await consumer.StopAsync();
        await ReportAsync();
        Log($"stopped after processing {Interlocked.Read(ref processed)}");
        return 0;
    }

    private static async Task<int> ConsumeSessionsAsync(Settings settings, DaprMQClient client)
    {
        long processed = 0;
        int busy = 0;
        long firstHandledMs = 0;
        long lastHandledMs = 0;
        var latency = new LatencyHistogram();
        var order = new SessionOrderTracker();
        var pod = Environment.GetEnvironmentVariable("HOSTNAME") ?? Guid.NewGuid().ToString("N");
        long seq = 0;
        var options = new SessionQueueConsumerOptions
        {
            MaxConcurrentSessions = settings.ConcurrentSessions,
            PrefetchCount = settings.Prefetch,
            LeaseSeconds = settings.LeaseSeconds,
            SessionIdleTimeoutSeconds = settings.SessionIdleTimeoutSeconds
        };
        await using var consumer = new SessionQueueConsumer(client, settings.QueueId, options, async (ctx, ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.CompareExchange(ref firstHandledMs, now, 0);
            if (ctx.Item.TryGetProperty("publishedAt", out var publishedAt))
            {
                latency.Record(now - publishedAt.GetInt64());
            }
            order.Observe(ctx.SessionId, ctx.Item.GetProperty("seq").GetInt64());
            Interlocked.Increment(ref busy);
            try
            {
                if (settings.ItemDelayMs > 0)
                {
                    await Task.Delay(settings.ItemDelayMs, ct);
                }
                Interlocked.Increment(ref processed);
                Interlocked.Exchange(ref lastHandledMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            finally
            {
                Interlocked.Decrement(ref busy);
            }
        });

        async Task ReportAsync()
        {
            var first = Interlocked.Read(ref firstHandledMs);
            var last = Interlocked.Read(ref lastHandledMs);
            var report = new ConsumerStatsReport(pod, ++seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Interlocked.Read(ref processed), Volatile.Read(ref busy), first == 0 ? null : first, latency.Snapshot())
            {
                LastHandledMs = last == 0 ? null : last,
                FifoViolations = order.FifoViolations
            };
            try
            {
                await client.EnqueueAsync(StatsQueue(settings), [new EnqueueItemDto(report)]);
            }
            catch (Exception ex)
            {
                Log($"stats report failed: {ex.Message}"); // the next report carries the same totals
            }
        }

        using var stopping = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });

        Log($"consuming sessions of {settings.QueueId}: {settings.ConcurrentSessions} at a time, prefetch {settings.Prefetch}, {settings.ItemDelayMs}ms per message, lease {settings.LeaseSeconds}s, idle timeout {settings.SessionIdleTimeoutSeconds}s");
        await consumer.StartAsync();
        try
        {
            var nextLog = DateTime.UtcNow;
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(StatsInterval, stopping.Token);
                await ReportAsync();
                if (DateTime.UtcNow >= nextLog)
                {
                    Log($"processed {Interlocked.Read(ref processed)}, {order.Redeliveries} redelivered, {order.FifoViolations} out of order");
                    nextLog = DateTime.UtcNow.AddSeconds(30);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await consumer.StopAsync();
        await ReportAsync();
        Log($"stopped after processing {Interlocked.Read(ref processed)}");
        return 0;
    }

    private static async Task<int> RunAsync(Settings settings, DaprMQClient client)
    {
        using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await client.WaitForReadyAsync(ct: ready.Token);

        using var channel = GrpcChannel.ForAddress(settings.ScalerAddress);
        var scaler = new ExternalScaler.ExternalScalerClient(channel);
        var sessions = settings.Workload == "sessions";
        // Messages: ready + locked. Sessions: how many sessions have work, which falls as a consumer releases
        // each drained session, or otherwise once it has stayed empty for the directory's debounce (30s).
        async Task<double> DepthAsync(bool includeDeadLetter)
        {
            var request = new GetMetricsRequest
            {
                ScaledObjectRef = new ScaledObjectRef { Name = settings.ScaledObjectName, Namespace = settings.ScaledObjectNamespace }
            };
            request.ScaledObjectRef.ScalerMetadata.Add("queueId", settings.QueueId);
            if (sessions)
            {
                request.ScaledObjectRef.ScalerMetadata.Add("mode", "sessions");
            }
            else
            {
                request.ScaledObjectRef.ScalerMetadata.Add("includeDeadLetter", includeDeadLetter ? "true" : "false");
            }
            var response = await scaler.GetMetricsAsync(request);
            return response.MetricValues.Single().MetricValueFloat;
        }

        Log(sessions
            ? $"publishing {settings.Sessions} sessions of {settings.MessagesPerSession} (length jitter {settings.SessionLengthJitterPercent}%, over {settings.PublishJitterSeconds}s) to {settings.QueueId} from {settings.Publishers} publishers, {settings.PublishBatch} per call"
            : $"publishing {settings.Messages} messages to {settings.QueueId} from {settings.Publishers} publishers, {settings.PublishBatch} per call");
        var startedAt = DateTimeOffset.UtcNow;
        var startedAtMs = startedAt.ToUnixTimeMilliseconds();
        var clock = Stopwatch.StartNew();
        var publishing = sessions ? PublishSessionsAsync(settings, client) : PublishAsync(settings, client);
        var tracker = new DrainTracker();
        var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        TimeSpan? published = null;
        var nextLog = TimeSpan.Zero;

        while (!tracker.Drained && clock.Elapsed < timeout)
        {
            await Task.Delay(settings.PollMs);
            if (publishing.IsCompleted && published == null)
            {
                await publishing; // surface a publish failure
                published = clock.Elapsed;
                Log($"published in {published.Value.TotalSeconds:0.0}s");
            }

            double depth;
            try
            {
                depth = await DepthAsync(includeDeadLetter: false);
            }
            catch (Exception ex)
            {
                Log($"depth read failed: {ex.Message}");
                continue;
            }
            // A session only counts once it has registered with the session directory, so for sessions a 0
            // isn't trusted until a session has been seen.
            tracker.Observe(depth, clock.Elapsed, publishingDone: published != null && (!sessions || tracker.PeakDepth > 0));
            if (clock.Elapsed >= nextLog)
            {
                Log(sessions ? $"depth {depth} (sessions with work)" : $"depth {depth} (ready + locked)");
                nextLog = clock.Elapsed + TimeSpan.FromSeconds(15);
            }
        }

        // Sessions have a DLQ each, so they aren't read: the handler never fails, and any would be missing.
        var deadLettered = sessions ? 0 : await DepthAsync(includeDeadLetter: true) - await DepthAsync(includeDeadLetter: false);

        // Every consumer reports every few seconds; wait for a round after the drain, then read them all.
        await Task.Delay(StatsInterval * 2 + TimeSpan.FromSeconds(2));
        var reports = await ReadStatsAsync(settings, client);
        var latest = ConsumerStats.Latest(reports);
        var endMs = sessions && ConsumerStats.LastHandledMs(latest) is { } lastMs
            ? lastMs
            : startedAtMs + (long)(tracker.DrainedAt ?? clock.Elapsed).TotalMilliseconds;
        var timeline = ConsumerStats.Timeline(reports, startedAtMs, (int)StatsInterval.TotalMilliseconds, endMs);
        var firstHandled = ConsumerStats.FirstHandledMs(latest);
        // Sessions: to the last message handled, as the SDK harnesses' session drains measure it. The scaler's
        // 0 can trail it (by up to the directory's debounce), so it's kept as scalerDrainSeconds instead.
        var wallClock = sessions && tracker.Drained && ConsumerStats.LastHandledMs(latest) is { } last
            ? (last - startedAtMs) / 1000.0
            : tracker.DrainedAt?.TotalSeconds;

        var measurements = new RunMeasurements(
            Drained: tracker.Drained,
            PublishSeconds: published?.TotalSeconds,
            WallClockSeconds: wallClock,
            PeakDepth: tracker.PeakDepth,
            DeadLettered: deadLettered,
            MessagesHandled: latest.Sum(r => r.Handled),
            TimeToFirstMessageSeconds: firstHandled is { } f ? (f - startedAtMs) / 1000.0 : null,
            PeakConcurrentHandlers: (int)timeline.BusyHandlers.DefaultIfEmpty(0).Max(),
            DeliveryLatencyMs: LatencyHistogram.Merge(latest.Select(r => r.Latency)).Summary(),
            Timeline: timeline)
        {
            FifoViolations = latest.Sum(r => r.FifoViolations),
            ScalerDrainSeconds = sessions ? tracker.DrainedAt?.TotalSeconds : null
        };
        Log($"{latest.Count} consumer pods reported, {measurements.MessagesHandled} messages handled");
        Console.WriteLine("RESULT " + RunRecord.Build(settings, measurements, startedAt).ToJsonString());
        return tracker.Drained ? 0 : 2;
    }

    /// <summary>Takes every stats report off the run's stats queue.</summary>
    private static async Task<List<ConsumerStatsReport>> ReadStatsAsync(Settings settings, DaprMQClient client)
    {
        var reports = new List<ConsumerStatsReport>();
        while (true)
        {
            var batch = await client.DequeueLockedAsync(StatsQueue(settings), count: 1000, allowCompetingConsumers: true);
            if (batch == null || batch.Items.Count == 0)
            {
                return reports;
            }
            reports.AddRange(batch.Items.Select(i => i.Item.Deserialize<ConsumerStatsReport>(JsonSerializerOptions.Web)!));
            await client.AcknowledgeBatchAsync(StatsQueue(settings), batch.Items.Select(i => i.LockId).ToList());
        }
    }

    private static Task PublishAsync(Settings settings, DaprMQClient client) =>
        Task.WhenAll(Enumerable.Range(0, settings.Publishers).Select(async publisher =>
        {
            // Publisher p takes messages p, p + publishers, ...: the backlog interleaves them.
            var mine = Enumerable.Range(0, settings.Messages).Where(n => n % settings.Publishers == publisher);
            foreach (var batch in mine.Chunk(settings.PublishBatch))
            {
                var publishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var items = batch.Select(n => new EnqueueItemDto(new { publisher, n, publishedAt })).ToList();
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await client.EnqueueAsync(settings.QueueId, items);
                        break;
                    }
                    catch (Exception ex) when (attempt < 5)
                    {
                        Log($"publisher {publisher}: enqueue failed ({ex.Message}), retrying");
                        await Task.Delay(TimeSpan.FromSeconds(attempt));
                    }
                }
            }
        }));

    /// <summary>
    /// Publisher p takes sessions p, p + publishers, ... in the order the plan publishes them (SessionPlan),
    /// waiting for each one's publish time. A session's messages go in order, seq 0..n-1, in calls of
    /// PublishBatch to that one session, so every message's place in its session is known.
    /// </summary>
    private static Task PublishSessionsAsync(Settings settings, DaprMQClient client)
    {
        var plan = SessionPlan.Build(settings);
        var clock = Stopwatch.StartNew();
        return Task.WhenAll(Enumerable.Range(0, settings.Publishers).Select(async publisher =>
        {
            foreach (var planned in plan.Where(p => p.Index % settings.Publishers == publisher).OrderBy(p => p.PublishAtSeconds))
            {
                var wait = TimeSpan.FromSeconds(planned.PublishAtSeconds) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait);
                }

                var session = planned.Index;
                var sessionId = $"s{session}";
                foreach (var batch in Enumerable.Range(0, planned.Messages).Chunk(settings.PublishBatch))
                {
                    var publishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var items = batch.Select(seq => new EnqueueItemDto(new { session, seq, publishedAt }, SessionId: sessionId)).ToList();
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            await client.EnqueueAsync(settings.QueueId, items);
                            break;
                        }
                        catch (Exception ex) when (attempt < 5)
                        {
                            Log($"publisher {publisher}: enqueue to {sessionId} failed ({ex.Message}), retrying");
                            await Task.Delay(TimeSpan.FromSeconds(attempt));
                        }
                    }
                }
            }
        }));
    }
}
