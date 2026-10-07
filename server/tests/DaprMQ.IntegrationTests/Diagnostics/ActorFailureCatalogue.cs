using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Diagnostics;

/// <summary>
/// Phase 0 of proposals/readiness-and-retries.md: causes each actor-call failure mode on a split
/// stack and records what the caller sees and what the gateway logged, so failures can be
/// classified as not-delivered / unknown from observed daprd behaviour, not assumptions.
///
/// Opt-in diagnostic, not a regression test (it returns immediately otherwise):
///   DAPRMQ_FAILURE_CATALOGUE=/path/to/catalogue.md dotnet test --filter ActorFailureCatalogue
/// </summary>
[Collection("Dapr Split Topology Collection")]
public class ActorFailureCatalogue(SplitTopologyFixture fixture)
{
    private readonly StringBuilder _report = new();

    [Fact]
    public async Task Run()
    {
        var outPath = Environment.GetEnvironmentVariable("DAPRMQ_FAILURE_CATALOGUE");
        if (string.IsNullOrEmpty(outPath))
        {
            return;
        }

        var env = fixture.Environment;
        _report.AppendLine("# Actor-call failure catalogue").AppendLine()
            .AppendLine($"daprd 1.18.4, split topology (gateway `daprmq-gateway` → worker `daprmq-api`), {DateTime.UtcNow:u}").AppendLine();

        await ModeAsync("0. Control: worker healthy", null, () => EnqueueAsync(NewQueue()));

        var warm = NewQueue();
        await EnqueueAsync(warm); // activate on the worker, so the gateway's table has it

        await ModeAsync("1a. Workers stopped gracefully, request immediately (before placement drops them)",
            async () => await env.StopWorkersAsync(),
            () => EnqueueAsync(warm), restore: env.StartWorkersAsync);

        await ModeAsync("1b. Workers stopped, request after placement dropped them (15 s)",
            async () => { await env.StopWorkersAsync(); await Task.Delay(TimeSpan.FromSeconds(15)); },
            () => EnqueueAsync(NewQueue()), restore: env.StartWorkersAsync);

        foreach (var (mode, sidecar) in new[] { ("2a. Worker app killed (SIGKILL) mid bulk enqueue of 10000", false), ("2b. Worker sidecar killed (SIGKILL) mid bulk enqueue of 10000", true) })
        {
            var bulk = NewQueue();
            await ModeAsync(mode, null, () => DuringAsync(BulkEnqueueAsync(bulk, 10_000), () => env.KillWorkersAsync(sidecar)),
                restore: RestartWorkersAsync);
            if (_report.ToString().Contains(mode))
            {
                _report.AppendLine($"**Items stored after recovery:** {await CountAndDrainAsync(bulk)} of 10000").AppendLine();
            }
        }

        await ModeAsync("3. Worker app hung (paused), sidecar up",
            env.PauseWorkerAppsAsync, () => EnqueueAsync(warm, TimeSpan.FromSeconds(150)), restore: env.UnpauseWorkerAppsAsync);

        await ModeAsync("4a. Placement stopped, actor already active",
            async () => { await env.StopPlacementAsync(); await Task.Delay(TimeSpan.FromSeconds(10)); },
            () => EnqueueAsync(warm, TimeSpan.FromSeconds(150)), restore: null);
        await ModeAsync("4b. Placement stopped, new actor",
            null, () => EnqueueAsync(NewQueue(), TimeSpan.FromSeconds(150)),
            restore: async () => { await env.StartPlacementAsync(); await Task.Delay(TimeSpan.FromSeconds(10)); });

        await File.WriteAllTextAsync(outPath, _report.ToString());
    }

    private async Task RestartWorkersAsync()
    {
        await fixture.Environment.StopWorkersAsync();
        await fixture.Environment.StartWorkersAsync();
    }

    private async Task ModeAsync(string name, Func<Task>? cause, Func<Task<string>> request, Func<Task>? restore = null)
    {
        // DAPRMQ_FAILURE_CATALOGUE_MODES=2 runs only the modes whose name starts with "2" (plus the control).
        if (Environment.GetEnvironmentVariable("DAPRMQ_FAILURE_CATALOGUE_MODES") is { Length: > 0 } only
            && !name.StartsWith(only) && !name.StartsWith('0'))
        {
            return;
        }

        var logBefore = (await fixture.Environment.GetApiServerLogsAsync()).Length;
        string outcome;
        try
        {
            if (cause != null)
            {
                await cause();
            }

            outcome = await request();
        }
        catch (Exception ex)
        {
            outcome = $"harness exception: {ex.GetType().Name}: {ex.Message}";
        }

        await Task.Delay(TimeSpan.FromSeconds(1)); // let the gateway flush its log
        var log = (await fixture.Environment.GetApiServerLogsAsync())[logBefore..];
        var relevant = log.Split('\n')
            .Where(l => l.Contains("Exception") || l.Contains("fail:") || l.Contains("Status(") || l.Contains("error", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Length > 400 ? l[..400] + "…" : l)
            .Distinct()
            .Take(25);

        _report.AppendLine($"## {name}").AppendLine()
            .AppendLine($"**Caller saw:** {outcome}").AppendLine()
            .AppendLine("**Gateway log:**").AppendLine("```")
            .AppendLine(string.Join('\n', relevant))
            .AppendLine("```").AppendLine();

        if (restore != null)
        {
            await restore();
            await WaitUntilAsync(async () => (await EnqueueAsync(NewQueue())).StartsWith("200"));
        }
    }

    /// <summary>Dequeues (without locks) until empty and returns how many items there were.</summary>
    private async Task<int> CountAndDrainAsync(string queueId)
    {
        var total = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
            request.Headers.Add("count", "1000");
            using var response = await fixture.GatewayClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return total;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var n = body.GetProperty("items").GetArrayLength();
            if (n == 0)
            {
                return total;
            }

            total += n;
        }
    }

    private static string NewQueue() => $"catalogue-{Guid.NewGuid():N}";

    private Task<string> EnqueueAsync(string queueId, TimeSpan? timeout = null) => SendAsync(
        $"/queue/{queueId}/enqueue", new { items = new[] { new { item = new { n = 1 } } } }, timeout);

    private Task<string> BulkEnqueueAsync(string queueId, int count) => SendAsync(
        $"/queue/{queueId}/enqueue", new { items = Enumerable.Range(0, count).Select(i => new { item = new { n = i, pad = new string('x', 2000) } }) },
        TimeSpan.FromSeconds(120));

    private async Task<string> SendAsync(string path, object body, TimeSpan? timeout)
    {
        using var client = new HttpClient { BaseAddress = fixture.GatewayClient.BaseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.PostAsJsonAsync(path, body);
            var text = await response.Content.ReadAsStringAsync();
            return $"{(int)response.StatusCode} after {sw.Elapsed.TotalSeconds:F1}s: {(text.Length > 300 ? text[..300] + "…" : text)}";
        }
        catch (Exception ex)
        {
            return $"client {ex.GetType().Name} after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}";
        }
    }

    private static async Task<string> DuringAsync(Task<string> call, Func<Task> fault)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await fault();
        return await call;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Stack did not recover within 2 minutes.");
            }

            await Task.Delay(1000);
        }
    }
}
