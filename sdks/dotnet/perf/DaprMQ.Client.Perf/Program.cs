using DaprMQ.Client;
using DaprMQ.Client.Perf;
using DaprMQ.IntegrationTests.Infrastructure;
using Grpc.Net.Client;

PerfOptions options;
try
{
    options = PerfOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

Directory.CreateDirectory(options.OutDir);

if (options.ReportOnly)
{
    Console.WriteLine($"Report: {HtmlReport.Write(options.OutDir)}");
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

DaprTestEnvironment? stack = null;
string httpEndpoint, grpcEndpoint, server;
if (options.HttpEndpoint != null)
{
    (httpEndpoint, grpcEndpoint) = (options.HttpEndpoint, options.GrpcEndpoint!);
    server = $"external {httpEndpoint}";
}
else
{
    var image = Environment.GetEnvironmentVariable("DAPRMQ_API_IMAGE") ?? "daprmq-api:test";
    Console.WriteLine($"Starting Testcontainers stack ({image})...");

    // DaprTestEnvironment mounts ../../../dapr-components relative to the working directory
    // (bin/<config>/<tfm> -> this project's copy), as it does under the test runner.
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    stack = new DaprTestEnvironment();
    try
    {
        await stack.InitializeAsync(null, image);
    }
    catch
    {
        await stack.DisposeAsync();
        throw;
    }
    (httpEndpoint, grpcEndpoint) = (stack.ApiServerUrl, stack.ApiServerGrpcUrl);
    server = $"testcontainers {image}";
}

try
{
    // The test stack speaks h2c (prior knowledge, no TLS).
    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    using var http = new HttpClient { BaseAddress = new Uri(httpEndpoint), Timeout = TimeSpan.FromMinutes(5) };
    using var channel = GrpcChannel.ForAddress(grpcEndpoint, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
    var client = new DaprMQClient(http, channel);

    await WaitForServerAsync(client, cts.Token);

    var environment = ResultStore.CaptureEnvironment(options.EnvLabel, server);
    var exitCode = 0;
    var runIds = new List<string>();

    foreach (var runOptions in options.Runs())
    {
        Console.WriteLine();
        Console.WriteLine($"=== Profile {runOptions.Profile} ===");
        var result = await new SessionDrainScenario(client, runOptions).RunAsync(cts.Token);

        var timestamp = DateTimeOffset.UtcNow;
        var runId = $"{timestamp:yyyyMMdd'T'HHmmss'Z'}_{runOptions.EnvLabel}_{runOptions.Profile}";
        var runPath = ResultStore.Save(options.OutDir, new RunRecord(1, runId, timestamp, environment, runOptions.Profile, runOptions.Scenario, result));
        runIds.Add(runId);

        PrintSummary(result, runOptions.MaxConcurrentSessions);
        Console.WriteLine($"Run:    {runPath}");

        // Lost or reordered messages make the timings meaningless - fail the run (and CI).
        if (result.Missing != 0 || result.FifoViolations != 0)
        {
            exitCode = 1;
        }
    }

    Console.WriteLine($"Report: {HtmlReport.Write(options.OutDir)}");

    var history = ResultStore.LoadHistory(options.OutDir);
    var comparisons = runIds
        .Select(id => RegressionCheck.Compare(history.Single(r => (string?)r["runId"] == id), history, options.BaselineBranch))
        .ToList();
    var markdown = RegressionCheck.ToMarkdown(comparisons, options.BaselineBranch);
    Console.WriteLine();
    Console.WriteLine(markdown);
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summaryPath)
    {
        File.AppendAllText(summaryPath, markdown + "\n");
    }

    if (exitCode == 0 && options.Gate && comparisons.Any(c => c.Regressed))
    {
        exitCode = 3;
    }

    return exitCode;
}
finally
{
    if (stack != null)
    {
        await stack.DisposeAsync();
    }
}

// Actors take a moment to register after the sidecar starts; wait on the gRPC health service.
static async Task WaitForServerAsync(DaprMQClient client, CancellationToken ct)
{
    using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    readyCts.CancelAfter(TimeSpan.FromMinutes(2));
    try
    {
        await client.WaitForReadyAsync(readyCts.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        throw new TimeoutException("Server did not report ready within 2 minutes.");
    }
}

static void PrintSummary(SessionDrainResult r, int slots)
{
    var p = r.Peak;
    Console.WriteLine();
    Console.WriteLine("=== Session drain ===");
    Console.WriteLine($"Seed:                 {r.SeedSeconds,10:F1} s");
    Console.WriteLine($"Wall clock:           {r.WallClockSeconds,10:F1} s   (ideal {r.IdealSeconds:F0} s, efficiency {r.Efficiency:P1})");
    Console.WriteLine($"First message after:  {r.TimeToFirstMessageSeconds,10:F2} s");
    Console.WriteLine($"Tail (no work left):  {r.TailSeconds,10:F1} s");
    Console.WriteLine($"Peak window:          {p.StartSeconds:F1}-{p.EndSeconds:F1} s, {slots} slots");
    Console.WriteLine($"  utilisation         {p.Utilization,10:P1}");
    Console.WriteLine($"  idle                {p.IdleSlotSeconds,10:F0} slot-s  ({p.IdleSlotSeconds / slots:F0} s per slot)");
    Console.WriteLine($"    drain wait        {p.DrainWaitSeconds,10:F0} slot-s");
    Console.WriteLine($"    claim             {p.ClaimSeconds,10:F0} slot-s");
    Console.WriteLine($"    in-session wait   {p.InSessionWaitSeconds,10:F0} slot-s");
    Console.WriteLine($"    between streams   {p.BetweenStreamsSeconds,10:F0} slot-s");
    Console.WriteLine($"Claim latency ms:     p50 {r.ClaimLatencyMs.P50:F0}  p95 {r.ClaimLatencyMs.P95:F0}  max {r.ClaimLatencyMs.Max:F0}");
    Console.WriteLine($"Drain wait ms:        p50 {r.DrainWaitMs.P50:F0}  p95 {r.DrainWaitMs.P95:F0}  max {r.DrainWaitMs.Max:F0}");
    Console.WriteLine($"Msg gap ms:           p50 {r.InterMessageGapMs.P50:F0}  p95 {r.InterMessageGapMs.P95:F0}  max {r.InterMessageGapMs.Max:F0}");
    Console.WriteLine($"Delivery ms:          p50 {r.DeliveryLatencyMs.P50:F0}  p95 {r.DeliveryLatencyMs.P95:F0}  max {r.DeliveryLatencyMs.Max:F0}  (enqueue -> handler start)");
    Console.WriteLine($"Streams {r.Streams} (failed claims {r.FailedClaims}, re-claimed sessions {r.SessionsClaimedMoreThanOnce})");
    Console.WriteLine($"Messages {r.MessagesHandled} (duplicates {r.Duplicates}, missing {r.Missing}, FIFO violations {r.FifoViolations})");
}
