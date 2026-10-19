using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DaprMQ.Client;
using Externalscaler;
using Grpc.Net.Client;

namespace ThroughputTest;

/// <summary>
/// The in-cluster throughput test. `consume` is a competing consumer (the chart runs it as a
/// KEDA-scaled Deployment); `run` publishes the backlog, waits for the consumers to drain it, and
/// prints one "RESULT {json}" line (the chart runs it as a Job). Settings come from the environment.
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

    private static async Task<int> RunAsync(Settings settings, DaprMQClient client)
    {
        using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await client.WaitForReadyAsync(ct: ready.Token);

        using var channel = GrpcChannel.ForAddress(settings.ScalerAddress);
        var scaler = new ExternalScaler.ExternalScalerClient(channel);
        async Task<double> DepthAsync(bool includeDeadLetter)
        {
            var request = new GetMetricsRequest
            {
                ScaledObjectRef = new ScaledObjectRef { Name = settings.ScaledObjectName, Namespace = settings.ScaledObjectNamespace }
            };
            request.ScaledObjectRef.ScalerMetadata.Add("queueId", settings.QueueId);
            request.ScaledObjectRef.ScalerMetadata.Add("includeDeadLetter", includeDeadLetter ? "true" : "false");
            var response = await scaler.GetMetricsAsync(request);
            return response.MetricValues.Single().MetricValueFloat;
        }

        Log($"publishing {settings.Messages} messages to {settings.QueueId} from {settings.Publishers} publishers, {settings.PublishBatch} per call");
        var startedAt = DateTimeOffset.UtcNow;
        var startedAtMs = startedAt.ToUnixTimeMilliseconds();
        var clock = Stopwatch.StartNew();
        var publishing = PublishAsync(settings, client);
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
            tracker.Observe(depth, clock.Elapsed, publishingDone: published != null);
            if (clock.Elapsed >= nextLog)
            {
                Log($"depth {depth} (ready + locked)");
                nextLog = clock.Elapsed + TimeSpan.FromSeconds(15);
            }
        }

        var deadLettered = await DepthAsync(includeDeadLetter: true) - await DepthAsync(includeDeadLetter: false);

        // Every consumer reports every few seconds; wait for a round after the drain, then read them all.
        await Task.Delay(StatsInterval * 2 + TimeSpan.FromSeconds(2));
        var reports = await ReadStatsAsync(settings, client);
        var latest = ConsumerStats.Latest(reports);
        var endMs = startedAtMs + (long)(tracker.DrainedAt ?? clock.Elapsed).TotalMilliseconds;
        var timeline = ConsumerStats.Timeline(reports, startedAtMs, (int)StatsInterval.TotalMilliseconds, endMs);
        var firstHandled = ConsumerStats.FirstHandledMs(latest);

        var measurements = new RunMeasurements(
            Drained: tracker.Drained,
            PublishSeconds: published?.TotalSeconds,
            WallClockSeconds: tracker.DrainedAt?.TotalSeconds,
            PeakDepth: tracker.PeakDepth,
            DeadLettered: deadLettered,
            MessagesHandled: latest.Sum(r => r.Handled),
            TimeToFirstMessageSeconds: firstHandled is { } f ? (f - startedAtMs) / 1000.0 : null,
            PeakConcurrentHandlers: (int)timeline.BusyHandlers.DefaultIfEmpty(0).Max(),
            DeliveryLatencyMs: LatencyHistogram.Merge(latest.Select(r => r.Latency)).Summary(),
            Timeline: timeline);
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
}
