using DaprMQ.Client;
using DaprMQ.Client.Perf;
using DaprMQ.IntegrationTests.Infrastructure;
using DaprMQ.PerfReport;
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

if (options.MigrateV1Dir != null)
{
    Console.WriteLine($"Converted {RunRecords.MigrateV1Directory(options.MigrateV1Dir)} schema-1 records in {options.MigrateV1Dir}");
    return 0;
}

// Results root shared by every SDK; this harness writes to {root}/sdk-dotnet.
var sdkDir = PerfResults.SdkDir(options.OutDir, "dotnet");
Directory.CreateDirectory(sdkDir);

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
    var topology = DaprTopology.Perf(options.ApiReplicas);
    Console.WriteLine($"Starting Testcontainers stack ({image}, {topology.ApiReplicas} API replica(s), {topology.SchedulerReplicas} schedulers)...");

    // DaprTestEnvironment mounts ../../../dapr-components relative to the working directory
    // (bin/<config>/<tfm> -> this project's copy), as it does under the test runner.
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    stack = new DaprTestEnvironment();
    try
    {
        // state-reads counts statements from Postgres' own log, so it needs log_statement=all.
        await stack.InitializeAsync(null, image, enableQueryInstrumentation: options.Benchmark == Benchmarks.StateReads, topology);
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
    using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1024 }) { BaseAddress = new Uri(httpEndpoint), Timeout = TimeSpan.FromMinutes(5) };
    using var channel = GrpcChannel.ForAddress(grpcEndpoint, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true } });
    var client = new DaprMQClient(http, channel);

    await WaitForServerAsync(client, cts.Token);

    var environment = RunEnvironment.Capture(options.EnvLabel, server);
    if (options.Benchmark == Benchmarks.StateReads)
    {
        return await RunStateReadsAsync(stack!, client, environment, sdkDir, options, cts.Token);
    }

    var exitCode = 0;
    var runIds = new List<string>();

    foreach (var runOptions in options.Runs())
    {
        Console.WriteLine();
        Console.WriteLine($"=== Profile {runOptions.Profile} ===");
        var context = new RunContext(DateTimeOffset.UtcNow, environment, runOptions.Scale, runOptions.ApiReplicas,
            stack?.Topology.SchedulerReplicas, RunContext.ClientVersion());

        System.Text.Json.Nodes.JsonObject record;
        if (runOptions.Load is { } load)
        {
            var result = await new LoadScenario(client, load).RunAsync(cts.Token);
            record = RunRecords.Load(context, runOptions.Profile, load, result);
        }
        else
        {
            var result = await new SessionDrainScenario(client, runOptions).RunAsync(cts.Token);
            record = RunRecords.SessionDrain(context, runOptions.Profile, runOptions.Scenario, result);
            PrintSummary(result, runOptions.MaxConcurrentSessions);
        }

        var runPath = PerfResults.Save(options.OutDir, record);
        runIds.Add(record["runId"]!.GetValue<string>());
        Console.WriteLine($"Run:    {runPath}");

        // Lost/reordered messages or failed operations make the timings meaningless - fail the run (and CI).
        if (record["checks"]!["passed"]!.GetValue<bool>() == false)
        {
            Console.WriteLine($"FAILED: {record["checks"]!["failures"]!.ToJsonString()}");
            exitCode = 1;
        }
    }

    Console.WriteLine($"Report: {HtmlReport.Write(options.OutDir)}");

    var (markdown, regressed) = Cli.Check(options.OutDir, runIds, options.BaselineBranch);
    Console.WriteLine();
    Console.WriteLine(markdown);
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summaryPath)
    {
        File.AppendAllText(summaryPath, markdown + "\n");
    }

    if (exitCode == 0 && options.Gate && regressed)
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

static async Task<int> RunStateReadsAsync(DaprTestEnvironment stack, DaprMQClient client, RunEnvironment environment, string outDir, PerfOptions options, CancellationToken ct)
{
    Console.WriteLine();
    Console.WriteLine("=== State reads ===");
    var steps = await new StateReadsScenario(stack, client).RunAsync(ct);

    var timestamp = DateTimeOffset.UtcNow;
    var runId = $"{timestamp:yyyyMMdd'T'HHmmss'Z'}_{options.EnvLabel}_state-reads";
    var runPath = StateReadsStore.Save(outDir, new StateReadsRunRecord(1, runId, timestamp, environment, steps));
    Console.WriteLine($"Run:    {runPath}");

    var history = StateReadsStore.LoadHistory(outDir);
    var comparisons = StateReadsRegressionCheck.Compare(history.Single(r => (string?)r["runId"] == runId), history, options.BaselineBranch);
    var markdown = RegressionCheck.ToMarkdown(comparisons, options.BaselineBranch, "Actor state reads", "Step");
    Console.WriteLine();
    Console.WriteLine(markdown);
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summaryPath)
    {
        File.AppendAllText(summaryPath, markdown + "\n");
    }

    return options.Gate && comparisons.Any(c => c.Regressed) ? 3 : 0;
}

// Actors take a moment to register after the sidecar starts; poll a probe enqueue until it lands.
static async Task WaitForServerAsync(DaprMQClient client, CancellationToken ct)
{
    var deadline = DateTime.UtcNow.AddMinutes(2);
    while (true)
    {
        try
        {
            var probe = await client.EnqueueAsync($"perf-probe-{Guid.NewGuid():N}", [new EnqueueItemDto(new { probe = true })], ct);
            if (probe.Success)
            {
                return;
            }
        }
        catch (Exception) when (DateTime.UtcNow < deadline && !ct.IsCancellationRequested) { }

        if (DateTime.UtcNow >= deadline)
        {
            throw new TimeoutException("Server did not accept a probe enqueue within 2 minutes.");
        }

        await Task.Delay(1000, ct);
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
