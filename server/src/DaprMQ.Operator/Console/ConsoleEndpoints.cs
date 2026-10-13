using System.Text;
using System.Text.Json;
using DaprMQ.Interfaces;

namespace DaprMQ.Operator.Console;

/// <summary>
/// The admin console's API (/api/console/*): a thin mirror of the workers' read-only
/// /internal/admin/* endpoints, plus a queue list that joins the catalog with depth. A worker's
/// reply (including 400/404/410) passes through unchanged; a transport failure, or a worker 5xx, is
/// a 503. List and detail reads are cached for every user; browse pages never are.
/// </summary>
public static class ConsoleEndpoints
{
    public const string Prefix = "/api/console";
    public const int DefaultQueueListLimit = 50;

    /// <summary>Two depth queries per row (the queue and its DLQ) must fit one /internal/queue-depth call.</summary>
    public const int MaxQueueListLimit = QueueDepthEndpointMaxQueries / 2;

    private const int QueueDepthEndpointMaxQueries = 100;

    /// <summary>The overview reads at most this many queues; past it, it says Truncated.</summary>
    public const int MaxOverviewQueues = 10_000;

    public const int OverviewTopQueues = 10;
    public const int OverviewDeadLetterQueues = 20;

    /// <summary>The worker's catalog read caps a page at this.</summary>
    private const int MaxCatalogPage = 1000;
    private const string WorkerPrefix = "internal/admin";
    private const string DeadLetterSuffix = "-deadletter";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Prefix);

        MapProxy(group, "/catalog", ["kind", "cursor", "limit"], cacheable: true);
        MapProxy(group, "/queues/{queueId}", [], cacheable: true);
        MapProxy(group, "/queues/{queueId}/messages", ["priority", "cursor", "limit"], cacheable: false);
        MapProxy(group, "/queues/{queueId}/messages/{location}", ["sequence"], cacheable: false);
        MapProxy(group, "/queues/{queueId}/messages/{location}/payload", ["sequence"], cacheable: false);
        MapProxy(group, "/queues/{queueId}/locks", ["cursor", "limit"], cacheable: false);
        MapProxy(group, "/queues/{queueId}/locks/{lockId}", [], cacheable: false);
        MapProxy(group, "/queues/{queueId}/locks/{lockId}/payload", [], cacheable: false);
        MapProxy(group, "/queues/{queueId}/sessions", ["cursor", "limit"], cacheable: false);
        MapProxy(group, "/topics/{topicId}", [], cacheable: true);

        group.MapGet("/overview", (IWorkerAdminClient client, ConsoleResponseCache cache, CancellationToken ct) =>
            HandleOverviewAsync(client, cache, ct));

        group.MapGet("/queues", (string? kind, string? cursor, int? limit, IWorkerAdminClient client, ConsoleResponseCache cache, CancellationToken ct) =>
            HandleQueuesAsync(kind, cursor, limit, client, cache, ct));

        // Anything else under /api is a 404, not the SPA's index.html.
        endpoints.MapFallback("/api/{**rest}", () => Results.NotFound());
        return endpoints;
    }

    private static void MapProxy(RouteGroupBuilder group, string pattern, string[] queryNames, bool cacheable)
    {
        group.MapGet(pattern, (HttpContext context, IWorkerAdminClient client, ConsoleResponseCache cache, CancellationToken ct) =>
        {
            // Route values arrive decoded; re-escape each so an id can't add path segments.
            var path = WorkerPrefix + pattern;
            foreach (var (name, value) in context.Request.RouteValues)
            {
                path = path.Replace($"{{{name}}}", Uri.EscapeDataString(value?.ToString() ?? string.Empty));
            }

            var query = queryNames
                .Where(name => context.Request.Query.ContainsKey(name))
                .Select(name => KeyValuePair.Create(name, context.Request.Query[name].ToString()))
                .ToList();

            return ProxyAsync(path, query, cacheable, client, cache, ct);
        });
    }

    public static async Task<IResult> ProxyAsync(
        string workerPath, IReadOnlyCollection<KeyValuePair<string, string>> query, bool cacheable,
        IWorkerAdminClient client, ConsoleResponseCache cache, CancellationToken cancellationToken)
    {
        try
        {
            var reply = cacheable
                ? await cache.GetOrAddAsync(CacheKey(workerPath, query), () => client.GetAsync(workerPath, query, CancellationToken.None), cancellationToken)
                : await client.GetAsync(workerPath, query, cancellationToken);
            return ToResult(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Unavailable(ex.Message);
        }
    }

    public static async Task<IResult> HandleQueuesAsync(
        string? kind, string? cursor, int? limit, IWorkerAdminClient client, ConsoleResponseCache cache, CancellationToken cancellationToken)
    {
        var listKind = CatalogEntryKind.Queue;
        if (!string.IsNullOrEmpty(kind)
            && !(Enum.TryParse(kind, ignoreCase: true, out listKind) && listKind is CatalogEntryKind.Queue or CatalogEntryKind.DeadLetter))
        {
            return Results.BadRequest("kind must be Queue or DeadLetter");
        }

        var pageSize = limit ?? DefaultQueueListLimit;
        if (pageSize is < 1 or > MaxQueueListLimit)
        {
            return Results.BadRequest($"limit must be between 1 and {MaxQueueListLimit}");
        }

        var query = new List<KeyValuePair<string, string>> { new("kind", listKind.ToString()), new("limit", pageSize.ToString()) };
        if (!string.IsNullOrEmpty(cursor))
        {
            query.Add(new("cursor", cursor));
        }

        try
        {
            var reply = await cache.GetOrAddAsync(CacheKey("queues", query), () => ReadQueuePageAsync(query, client), cancellationToken);
            return ToResult(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Unavailable(ex.Message);
        }
    }

    /// <summary>
    /// The overview from committed state: every catalogued queue's depth and its DLQ's, through the
    /// same depth read as the queue list, summed and ranked. Its cost grows with the number of queues
    /// (one catalog read per 1,000, one depth call per 50), so it's capped at
    /// <paramref name="maxQueues"/> and cached for every caller like other polled reads.
    /// </summary>
    public static async Task<IResult> HandleOverviewAsync(
        IWorkerAdminClient client, ConsoleResponseCache cache, CancellationToken cancellationToken, int maxQueues = MaxOverviewQueues)
    {
        try
        {
            var reply = await cache.GetOrAddAsync("overview", () => ReadOverviewAsync(client, maxQueues), cancellationToken);
            return ToResult(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Unavailable(ex.Message);
        }
    }

    private static async Task<WorkerReply> ReadOverviewAsync(IWorkerAdminClient client, int maxQueues)
    {
        var queueIds = new List<string>();
        Dictionary<CatalogEntryKind, int> totals = [];
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("kind", CatalogEntryKind.Queue.ToString()),
                new("limit", Math.Min(MaxCatalogPage, maxQueues - queueIds.Count).ToString())
            };
            if (cursor != null)
            {
                query.Add(new("cursor", cursor));
            }

            var reply = await client.GetAsync($"{WorkerPrefix}/catalog", query, CancellationToken.None);
            if (reply.StatusCode != StatusCodes.Status200OK)
            {
                return reply;
            }

            var page = JsonSerializer.Deserialize<CatalogPage>(reply.Body, JsonOptions)!;
            totals = page.Totals;
            queueIds.AddRange(page.Entries.Select(e => e.Id));
            cursor = page.NextCursor;
        }
        while (cursor != null && queueIds.Count < maxQueues);

        // Two queries per queue (it and its DLQ), split to the depth endpoint's limit; the worker
        // client's concurrency limit bounds how many run at once.
        var responses = await Task.WhenAll(queueIds
            .Chunk(QueueDepthEndpointMaxQueries / 2)
            .Select(chunk => client.GetDepthsAsync(new QueueDepthRequest
            {
                Queries = chunk.SelectMany(id => new[] { id, id + DeadLetterSuffix }).Select(id => new QueueDepthQuery { QueueId = id }).ToList()
            }, CancellationToken.None)));
        var depths = responses.SelectMany(r => r.Results).ToDictionary(r => r.QueueId);

        var errors = 0;
        ConsoleQueueDepth DepthOf(string id, string rowId)
        {
            var depth = depths.GetValueOrDefault(id);
            if (depth == null || depth.Error != null)
            {
                errors++;
                return new ConsoleQueueDepth { Id = rowId };
            }

            return new ConsoleQueueDepth { Id = rowId, Ready = depth.Ready, Locked = depth.Locked };
        }

        var queues = queueIds.Select(id => (Queue: DepthOf(id, id), DeadLetter: DepthOf(id + DeadLetterSuffix, id))).ToList();
        var deadLetters = queues.Select(q => q.DeadLetter).Where(d => d.Ready + d.Locked > 0).ToList();
        static List<ConsoleQueueDepth> Top(IEnumerable<ConsoleQueueDepth> rows, int count) => rows
            .Where(r => r.Ready + r.Locked > 0)
            .OrderByDescending(r => r.Ready + r.Locked)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();

        var overview = new ConsoleOverview
        {
            Totals = totals,
            Ready = queues.Sum(q => q.Queue.Ready),
            Locked = queues.Sum(q => q.Queue.Locked),
            DeadLettered = deadLetters.Sum(d => d.Ready + d.Locked),
            DeadLetterQueuesWithMessages = deadLetters.Count,
            TopQueues = Top(queues.Select(q => q.Queue), OverviewTopQueues),
            DeadLetterQueues = Top(deadLetters, OverviewDeadLetterQueues),
            QueuesRead = queueIds.Count,
            Truncated = cursor != null,
            Errors = errors
        };

        return new WorkerReply(StatusCodes.Status200OK, JsonSerializer.Serialize(overview, JsonOptions));
    }

    private static async Task<WorkerReply> ReadQueuePageAsync(IReadOnlyCollection<KeyValuePair<string, string>> query, IWorkerAdminClient client)
    {
        var catalogReply = await client.GetAsync($"{WorkerPrefix}/catalog", query, CancellationToken.None);
        if (catalogReply.StatusCode != StatusCodes.Status200OK)
        {
            return catalogReply;
        }

        var catalog = JsonSerializer.Deserialize<CatalogPage>(catalogReply.Body, JsonOptions)!;
        var queries = catalog.Entries
            .SelectMany(e => e.Kind == CatalogEntryKind.Queue ? [e.Id, e.Id + DeadLetterSuffix] : new[] { e.Id })
            .Select(id => new QueueDepthQuery { QueueId = id })
            .ToList();
        var depths = queries.Count == 0
            ? new Dictionary<string, QueueDepthResult>()
            : (await client.GetDepthsAsync(new QueueDepthRequest { Queries = queries }, CancellationToken.None))
                .Results.ToDictionary(r => r.QueueId);

        var page = new ConsoleQueuePage
        {
            NextCursor = catalog.NextCursor,
            Totals = catalog.Totals,
            Items = catalog.Entries.Select(e =>
            {
                var depth = depths.GetValueOrDefault(e.Id);
                var deadLetter = e.Kind == CatalogEntryKind.Queue ? depths.GetValueOrDefault(e.Id + DeadLetterSuffix) : null;
                return new ConsoleQueueRow
                {
                    Id = e.Id,
                    Kind = e.Kind,
                    ParentId = e.ParentId,
                    Ready = depth?.Ready ?? 0,
                    Locked = depth?.Locked ?? 0,
                    DeadLetterCount = e.Kind == CatalogEntryKind.Queue ? (deadLetter?.Ready ?? 0) + (deadLetter?.Locked ?? 0) : null,
                    Error = depth == null ? "no depth returned" : depth.Error ?? deadLetter?.Error
                };
            }).ToList()
        };

        return new WorkerReply(StatusCodes.Status200OK, JsonSerializer.Serialize(page, JsonOptions));
    }

    private static IResult ToResult(WorkerReply reply) =>
        reply.StatusCode >= 500
            ? Unavailable($"worker returned {reply.StatusCode}")
            : Results.Content(reply.Body, "application/json", Encoding.UTF8, reply.StatusCode);

    private static IResult Unavailable(string detail) =>
        Results.Problem(title: "Workers unavailable", detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string CacheKey(string path, IEnumerable<KeyValuePair<string, string>> query) =>
        path + "?" + string.Join("&", query.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => $"{q.Key}={q.Value}"));
}
