using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Grpc.Net.Client;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;
using ScottPlot;

// Enable HTTP/2 without TLS
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

// Default to Docker gateway ports, but allow override via environment variables
var httpBaseUrl = Environment.GetEnvironmentVariable("HTTP_ENDPOINT") ?? "http://localhost:8002";
var grpcBaseUrl = Environment.GetEnvironmentVariable("GRPC_ENDPOINT") ?? "http://localhost:8102";
var warmupIterations = int.Parse(Environment.GetEnvironmentVariable("WARMUP_ITERATIONS") ?? "10");
var testIterations = int.Parse(Environment.GetEnvironmentVariable("TEST_ITERATIONS") ?? "10000");
var virtualUsers = int.Parse(Environment.GetEnvironmentVariable("VIRTUAL_USERS") ?? "1");
var rampUpRate = double.Parse(Environment.GetEnvironmentVariable("RAMPUP_RATE_USERS_PER_SECOND") ?? "0"); // 0 = instant, 1 = 1 user/sec, 0.5 = 1 user/2sec

// Generate unique queue IDs for each virtual user (randomized on each run)
var runId = Guid.NewGuid().ToString("N")[..8];

// Create queue IDs for each virtual user
var httpQueueIds = Enumerable.Range(0, virtualUsers)
    .Select(userId => $"perf-test-http-{runId}-user{userId}")
    .ToList();

var grpcQueueIds = Enumerable.Range(0, virtualUsers)
    .Select(userId => $"perf-test-grpc-{runId}-user{userId}")
    .ToList();

// Create results directory if it doesn't exist
var resultsDir = Path.Combine(Directory.GetCurrentDirectory(), "results");
Directory.CreateDirectory(resultsDir);

Console.WriteLine("=== Push Operation Performance Test ===");
Console.WriteLine($"HTTP Endpoint: {httpBaseUrl}");
Console.WriteLine($"gRPC Endpoint: {grpcBaseUrl}");
Console.WriteLine($"Virtual users: {virtualUsers} (each with own queue)");
if (rampUpRate > 0)
{
    var totalRampUpTime = (virtualUsers - 1) / rampUpRate;
    Console.WriteLine($"Ramp-up rate: {rampUpRate} users/second (total ramp-up time: {totalRampUpTime:F1}s)");
}
else
{
    Console.WriteLine("Ramp-up rate: Instant (all users start simultaneously)");
}
if (virtualUsers <= 3)
{
    Console.WriteLine($"HTTP Queue IDs: {string.Join(", ", httpQueueIds)}");
    Console.WriteLine($"gRPC Queue IDs: {string.Join(", ", grpcQueueIds)}");
}
else
{
    Console.WriteLine($"HTTP Queue ID pattern: perf-test-http-{runId}-user[0-{virtualUsers - 1}]");
    Console.WriteLine($"gRPC Queue ID pattern: perf-test-grpc-{runId}-user[0-{virtualUsers - 1}]");
}
Console.WriteLine($"Warmup iterations: {warmupIterations} (per virtual user)");
Console.WriteLine($"Test iterations: {testIterations} (per virtual user)");
Console.WriteLine($"Total operations: {testIterations * virtualUsers} (per protocol)");
Console.WriteLine();

// Test payload
var testData = new { id = 1, name = "performance-test", timestamp = DateTime.UtcNow };
var testJson = JsonSerializer.Serialize(testData);

Console.WriteLine("Warming up...");
// Warmup with first virtual user's queue only
var warmupHttpClient = new HttpClient { BaseAddress = new Uri(httpBaseUrl) };
var warmupGrpcChannel = GrpcChannel.ForAddress(grpcBaseUrl);
var warmupGrpcClient = new GrpcService.DaprMQClient(warmupGrpcChannel);

for (int i = 0; i < warmupIterations; i++)
{
    await PushViaHttp(warmupHttpClient, httpQueueIds[0], testJson, priority: 1);
}

for (int i = 0; i < warmupIterations; i++)
{
    await PushViaGrpc(warmupGrpcClient, grpcQueueIds[0], testJson, priority: 1);
}

warmupHttpClient.Dispose();
warmupGrpcChannel.Dispose();

Console.WriteLine("Warmup complete. Starting performance tests...\n");

// Test HTTP Performance with multiple virtual users (with ramp-up)
Console.WriteLine($"Testing HTTP Push ({virtualUsers} virtual users × {testIterations} iterations = {virtualUsers * testIterations} total operations)...");
var httpStartTime = Stopwatch.GetTimestamp();

var httpUserTasks = Enumerable.Range(0, virtualUsers).Select(async userId =>
{
    // Calculate start delay based on ramp-up rate
    double startDelaySeconds = rampUpRate > 0 ? userId / rampUpRate : 0;
    int startDelayMs = (int)(startDelaySeconds * 1000);

    if (startDelayMs > 0)
    {
        await Task.Delay(startDelayMs);
    }

    // Record when this user actually started
    var userStartTime = Stopwatch.GetTimestamp();

    var httpClient = new HttpClient { BaseAddress = new Uri(httpBaseUrl) };
    var queueId = httpQueueIds[userId]; // Each user has own queue
    var timings = new List<(double timestamp, double latency, bool success, int statusCode)>();

    for (int i = 0; i < testIterations; i++)
    {
        var requestStart = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var (success, statusCode) = await PushViaHttp(httpClient, queueId, testJson, priority: 1);
        sw.Stop();
        // Timestamp relative to when this user started (not global start)
        var elapsedSeconds = (requestStart - userStartTime) / (double)Stopwatch.Frequency;
        // Add user's start delay to shift timeline
        var absoluteTimestamp = elapsedSeconds + startDelaySeconds;
        timings.Add((absoluteTimestamp, sw.Elapsed.TotalMilliseconds, success, statusCode));
    }

    httpClient.Dispose();
    return timings;
}).ToArray();

var httpAllTimings = await Task.WhenAll(httpUserTasks);
var httpTimings = httpAllTimings.SelectMany(t => t).ToList();

// Test gRPC Performance with multiple virtual users (with ramp-up)
Console.WriteLine($"Testing gRPC Push ({virtualUsers} virtual users × {testIterations} iterations = {virtualUsers * testIterations} total operations)...");
var grpcStartTime = Stopwatch.GetTimestamp();

var grpcUserTasks = Enumerable.Range(0, virtualUsers).Select(async userId =>
{
    // Calculate start delay based on ramp-up rate
    double startDelaySeconds = rampUpRate > 0 ? userId / rampUpRate : 0;
    int startDelayMs = (int)(startDelaySeconds * 1000);

    if (startDelayMs > 0)
    {
        await Task.Delay(startDelayMs);
    }

    // Record when this user actually started
    var userStartTime = Stopwatch.GetTimestamp();

    var grpcChannel = GrpcChannel.ForAddress(grpcBaseUrl);
    var grpcClient = new GrpcService.DaprMQClient(grpcChannel);
    var queueId = grpcQueueIds[userId]; // Each user has own queue
    var timings = new List<(double timestamp, double latency, bool success, int statusCode)>();

    for (int i = 0; i < testIterations; i++)
    {
        var requestStart = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        var (success, statusCode) = await PushViaGrpc(grpcClient, queueId, testJson, priority: 1);
        sw.Stop();
        // Timestamp relative to when this user started (not global start)
        var elapsedSeconds = (requestStart - userStartTime) / (double)Stopwatch.Frequency;
        // Add user's start delay to shift timeline
        var absoluteTimestamp = elapsedSeconds + startDelaySeconds;
        timings.Add((absoluteTimestamp, sw.Elapsed.TotalMilliseconds, success, statusCode));
    }

    grpcChannel.Dispose();
    return timings;
}).ToArray();

var grpcAllTimings = await Task.WhenAll(grpcUserTasks);
var grpcTimings = grpcAllTimings.SelectMany(t => t).ToList();

// Calculate statistics (extract just latencies for overall stats, only for successful requests)
var httpSuccessLatencies = httpTimings.Where(t => t.success).Select(t => t.latency).ToList();
var grpcSuccessLatencies = grpcTimings.Where(t => t.success).Select(t => t.latency).ToList();
var httpStats = CalculateStats(httpSuccessLatencies);
var grpcStats = CalculateStats(grpcSuccessLatencies);

// Calculate success/failure counts
var httpSuccessCount = httpTimings.Count(t => t.success);
var httpFailureCount = httpTimings.Count - httpSuccessCount;
var grpcSuccessCount = grpcTimings.Count(t => t.success);
var grpcFailureCount = grpcTimings.Count - grpcSuccessCount;

// Display results
Console.WriteLine("\n=== RESULTS ===");
Console.WriteLine($"(Aggregated across {virtualUsers} virtual user{(virtualUsers > 1 ? "s" : "")})\n");

Console.WriteLine("HTTP Push:");
Console.WriteLine($"  Total operations: {httpTimings.Count}");
Console.WriteLine($"  Successful:       {httpSuccessCount} ({(httpSuccessCount * 100.0 / httpTimings.Count):F2}%)");
Console.WriteLine($"  Failed:           {httpFailureCount} ({(httpFailureCount * 100.0 / httpTimings.Count):F2}%)");
if (httpSuccessCount > 0)
{
    Console.WriteLine($"  Min:              {httpStats.Min:F2} ms");
    Console.WriteLine($"  Max:              {httpStats.Max:F2} ms");
    Console.WriteLine($"  Mean:             {httpStats.Mean:F2} ms");
    Console.WriteLine($"  Median:           {httpStats.Median:F2} ms");
    Console.WriteLine($"  P95:              {httpStats.P95:F2} ms");
    Console.WriteLine($"  P99:              {httpStats.P99:F2} ms");
}

Console.WriteLine("\ngRPC Push:");
Console.WriteLine($"  Total operations: {grpcTimings.Count}");
Console.WriteLine($"  Successful:       {grpcSuccessCount} ({(grpcSuccessCount * 100.0 / grpcTimings.Count):F2}%)");
Console.WriteLine($"  Failed:           {grpcFailureCount} ({(grpcFailureCount * 100.0 / grpcTimings.Count):F2}%)");
if (grpcSuccessCount > 0)
{
    Console.WriteLine($"  Min:              {grpcStats.Min:F2} ms");
    Console.WriteLine($"  Max:              {grpcStats.Max:F2} ms");
    Console.WriteLine($"  Mean:             {grpcStats.Mean:F2} ms");
    Console.WriteLine($"  Median:           {grpcStats.Median:F2} ms");
    Console.WriteLine($"  P95:              {grpcStats.P95:F2} ms");
    Console.WriteLine($"  P99:              {grpcStats.P99:F2} ms");
}

if (httpSuccessCount > 0 && grpcSuccessCount > 0)
{
    Console.WriteLine("\nComparison:");
    var ratio = grpcStats.Mean / httpStats.Mean;
    if (ratio > 1.1)
    {
        Console.WriteLine($"  ⚠️  gRPC is {ratio:F2}x SLOWER than HTTP (expected to be similar or faster)");
    }
    else if (ratio < 0.9)
    {
        Console.WriteLine($"  ✅ gRPC is {(1 / ratio):F2}x FASTER than HTTP");
    }
    else
    {
        Console.WriteLine($"  ✅ gRPC and HTTP have similar performance (ratio: {ratio:F2})");
    }
}

// Generate performance graphs
Console.WriteLine("\nGenerating performance graphs...");
var latencyGraphPath = GeneratePerformanceGraph(httpTimings, grpcTimings, resultsDir, virtualUsers, rampUpRate);
Console.WriteLine($"  📊 Latency graph saved to: {latencyGraphPath}");

var successFailureGraphPath = GenerateSuccessFailureGraph(httpTimings, grpcTimings, resultsDir, virtualUsers, rampUpRate);
Console.WriteLine($"  📊 Success/Failure graph saved to: {successFailureGraphPath}");

// Helper methods
static async Task<(bool success, int statusCode)> PushViaHttp(HttpClient client, string queueId, string itemJson, int priority)
{
    try
    {
        var request = new
        {
            item = JsonSerializer.Deserialize<JsonElement>(itemJson),
            priority
        };

        var response = await client.PostAsJsonAsync($"/queue/{queueId}/push", request);
        var statusCode = (int)response.StatusCode;
        var isSuccess = statusCode >= 200 && statusCode < 300;
        return (isSuccess, statusCode);
    }
    catch (Exception)
    {
        return (false, 0); // Network error or other exception
    }
}

static async Task<(bool success, int statusCode)> PushViaGrpc(GrpcService.DaprMQClient client, string queueId, string itemJson, int priority)
{
    try
    {
        var request = new PushRequest
        {
            QueueId = queueId,
            ItemJson = itemJson,
            Priority = priority
        };

        var response = await client.PushAsync(request);
        // gRPC OK status = 0
        return (response.Success, 0); // 0 = OK in gRPC
    }
    catch (Grpc.Core.RpcException ex)
    {
        // Return gRPC status code
        return (false, (int)ex.StatusCode);
    }
    catch (Exception)
    {
        return (false, -1); // Non-gRPC exception
    }
}

static Stats CalculateStats(List<double> timings)
{
    var sorted = timings.OrderBy(t => t).ToList();
    return new Stats
    {
        Min = sorted.First(),
        Max = sorted.Last(),
        Mean = sorted.Average(),
        Median = sorted[sorted.Count / 2],
        P95 = sorted[(int)(sorted.Count * 0.95)],
        P99 = sorted[(int)(sorted.Count * 0.99)]
    };
}

static string GeneratePerformanceGraph(List<(double timestamp, double latency, bool success, int statusCode)> httpTimings, List<(double timestamp, double latency, bool success, int statusCode)> grpcTimings, string resultsDir, int virtualUsers, double rampUpRate)
{
    // Bucket data into 1-second intervals and calculate avg + P95 for each bucket (only successful requests)
    var httpBuckets = BucketTimeSeries([.. httpTimings.Where(t => t.success).Select(t => (t.timestamp, t.latency))]);
    var grpcBuckets = BucketTimeSeries([.. grpcTimings.Where(t => t.success).Select(t => (t.timestamp, t.latency))]);

    var plot = new Plot();

    // Plot HTTP Average (solid blue line)
    var httpAvgScatter = plot.Add.Scatter(
        httpBuckets.Select(b => b.timeSeconds).ToArray(),
        httpBuckets.Select(b => b.avg).ToArray()
    );
    httpAvgScatter.LegendText = "HTTP Avg";
    httpAvgScatter.Color = ScottPlot.Color.FromHex("#3498db"); // Blue
    httpAvgScatter.LineWidth = 2.0f;
    httpAvgScatter.MarkerSize = 0;

    // Plot HTTP P95 (dashed blue line)
    var httpP95Scatter = plot.Add.Scatter(
        httpBuckets.Select(b => b.timeSeconds).ToArray(),
        httpBuckets.Select(b => b.p95).ToArray()
    );
    httpP95Scatter.LegendText = "HTTP P95";
    httpP95Scatter.Color = ScottPlot.Color.FromHex("#3498db"); // Blue
    httpP95Scatter.LineWidth = 1.5f;
    httpP95Scatter.LinePattern = ScottPlot.LinePattern.Dashed;
    httpP95Scatter.MarkerSize = 0;

    // Plot gRPC Average (solid green line)
    var grpcAvgScatter = plot.Add.Scatter(
        grpcBuckets.Select(b => b.timeSeconds).ToArray(),
        grpcBuckets.Select(b => b.avg).ToArray()
    );
    grpcAvgScatter.LegendText = "gRPC Avg";
    grpcAvgScatter.Color = ScottPlot.Color.FromHex("#2ecc71"); // Green
    grpcAvgScatter.LineWidth = 2.0f;
    grpcAvgScatter.MarkerSize = 0;

    // Plot gRPC P95 (dashed green line)
    var grpcP95Scatter = plot.Add.Scatter(
        grpcBuckets.Select(b => b.timeSeconds).ToArray(),
        grpcBuckets.Select(b => b.p95).ToArray()
    );
    grpcP95Scatter.LegendText = "gRPC P95";
    grpcP95Scatter.Color = ScottPlot.Color.FromHex("#2ecc71"); // Green
    grpcP95Scatter.LineWidth = 1.5f;
    grpcP95Scatter.LinePattern = ScottPlot.LinePattern.Dashed;
    grpcP95Scatter.MarkerSize = 0;

    // Configure plot
    var userText = virtualUsers > 1 ? $"{virtualUsers} virtual users" : "1 virtual user";
    var rampUpText = rampUpRate > 0 ? $", ramp-up: {rampUpRate}/s" : "";
    plot.Title($"HTTP vs gRPC Push Performance ({userText}{rampUpText}, 1s buckets)");
    plot.XLabel("Time (seconds)");
    plot.YLabel("Latency (ms)");
    plot.ShowLegend();

    // Add grid for readability
    plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#e0e0e0");

    // Generate timestamped filename
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    var filename = $"performance-results_{timestamp}.png";
    var outputPath = Path.Combine(resultsDir, filename);

    plot.SavePng(outputPath, 1400, 700);

    return outputPath;
}

static string GenerateSuccessFailureGraph(List<(double timestamp, double latency, bool success, int statusCode)> httpTimings, List<(double timestamp, double latency, bool success, int statusCode)> grpcTimings, string resultsDir, int virtualUsers, double rampUpRate)
{
    // Bucket data into 1-second intervals and count success/failures
    var httpBuckets = BucketSuccessFailure(httpTimings);
    var grpcBuckets = BucketSuccessFailure(grpcTimings);

    var plot = new Plot();

    // Helper function to apply log scale (log10(x + 1) to handle zero values)
    static double LogScale(int value) => Math.Log10(value + 1);

    // Plot HTTP Success (solid blue line) with log scale
    var httpSuccessScatter = plot.Add.Scatter(
        httpBuckets.Select(b => b.timeSeconds).ToArray(),
        httpBuckets.Select(b => LogScale(b.successCount)).ToArray()
    );
    httpSuccessScatter.LegendText = "HTTP Success";
    httpSuccessScatter.Color = ScottPlot.Color.FromHex("#3498db"); // Blue
    httpSuccessScatter.LineWidth = 2.0f;
    httpSuccessScatter.MarkerSize = 0;

    // Plot HTTP Failures (dashed red line) with log scale
    var httpFailureScatter = plot.Add.Scatter(
        httpBuckets.Select(b => b.timeSeconds).ToArray(),
        httpBuckets.Select(b => LogScale(b.failureCount)).ToArray()
    );
    httpFailureScatter.LegendText = "HTTP Failures";
    httpFailureScatter.Color = ScottPlot.Color.FromHex("#e74c3c"); // Red
    httpFailureScatter.LineWidth = 1.5f;
    httpFailureScatter.LinePattern = ScottPlot.LinePattern.Dashed;
    httpFailureScatter.MarkerSize = 0;

    // Plot gRPC Success (solid green line) with log scale
    var grpcSuccessScatter = plot.Add.Scatter(
        grpcBuckets.Select(b => b.timeSeconds).ToArray(),
        grpcBuckets.Select(b => LogScale(b.successCount)).ToArray()
    );
    grpcSuccessScatter.LegendText = "gRPC Success";
    grpcSuccessScatter.Color = ScottPlot.Color.FromHex("#2ecc71"); // Green
    grpcSuccessScatter.LineWidth = 2.0f;
    grpcSuccessScatter.MarkerSize = 0;

    // Plot gRPC Failures (dashed orange line) with log scale
    var grpcFailureScatter = plot.Add.Scatter(
        grpcBuckets.Select(b => b.timeSeconds).ToArray(),
        grpcBuckets.Select(b => LogScale(b.failureCount)).ToArray()
    );
    grpcFailureScatter.LegendText = "gRPC Failures";
    grpcFailureScatter.Color = ScottPlot.Color.FromHex("#f39c12"); // Orange
    grpcFailureScatter.LineWidth = 1.5f;
    grpcFailureScatter.LinePattern = ScottPlot.LinePattern.Dashed;
    grpcFailureScatter.MarkerSize = 0;

    // Configure plot
    var userText = virtualUsers > 1 ? $"{virtualUsers} virtual users" : "1 virtual user";
    var rampUpText = rampUpRate > 0 ? $", ramp-up: {rampUpRate}/s" : "";
    plot.Title($"HTTP vs gRPC Success/Failure Counts ({userText}{rampUpText}, 1s buckets)");
    plot.XLabel("Time (seconds)");
    plot.YLabel("Log10(Request Count + 1)");
    plot.ShowLegend();

    // Add grid for readability
    plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#e0e0e0");

    // Generate timestamped filename
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    var filename = $"success-failure-results_{timestamp}.png";
    var outputPath = Path.Combine(resultsDir, filename);

    plot.SavePng(outputPath, 1400, 700);

    return outputPath;
}

static List<(double timeSeconds, int successCount, int failureCount)> BucketSuccessFailure(List<(double timestamp, double latency, bool success, int statusCode)> timings)
{
    if (timings.Count == 0)
        return [];

    // Find the max timestamp to determine number of buckets needed
    var maxTime = timings.Max(t => t.timestamp);
    var numBuckets = (int)Math.Ceiling(maxTime) + 1;

    var buckets = new List<(double timeSeconds, int successCount, int failureCount)>();

    for (int bucketIndex = 0; bucketIndex < numBuckets; bucketIndex++)
    {
        // Get all requests that fall within this 1-second bucket
        var bucketRequests = timings
            .Where(t => t.timestamp >= bucketIndex && t.timestamp < bucketIndex + 1)
            .ToList();

        if (bucketRequests.Count == 0)
            continue; // Skip empty buckets

        var successCount = bucketRequests.Count(r => r.success);
        var failureCount = bucketRequests.Count - successCount;

        buckets.Add((bucketIndex, successCount, failureCount));
    }

    return buckets;
}

static List<(double timeSeconds, double avg, double p95)> BucketTimeSeries(List<(double timestamp, double latency)> timings)
{
    if (timings.Count == 0)
        return [];

    // Find the max timestamp to determine number of buckets needed
    var maxTime = timings.Max(t => t.timestamp);
    var numBuckets = (int)Math.Ceiling(maxTime) + 1;

    var buckets = new List<(double timeSeconds, double avg, double p95)>();

    for (int bucketIndex = 0; bucketIndex < numBuckets; bucketIndex++)
    {
        // Get all latencies that fall within this 1-second bucket
        var bucketLatencies = timings
            .Where(t => t.timestamp >= bucketIndex && t.timestamp < bucketIndex + 1)
            .Select(t => t.latency)
            .OrderBy(l => l)
            .ToList();

        if (bucketLatencies.Count == 0)
            continue; // Skip empty buckets

        var avg = bucketLatencies.Average();
        var p95Index = (int)(bucketLatencies.Count * 0.95);
        var p95 = bucketLatencies[Math.Min(p95Index, bucketLatencies.Count - 1)];

        buckets.Add((bucketIndex, avg, p95));
    }

    return buckets;
}

record Stats
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
}
