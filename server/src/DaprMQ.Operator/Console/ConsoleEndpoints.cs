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

        group.MapGet("/metrics/{name}", (string name, string? id, string? ids, string? range, IServiceProvider services,
                ConsoleResponseCache cache, TimeProvider time, CancellationToken ct) =>
            HandleMetricsAsync(name, id, ids, range, services.GetService<IPrometheusClient>(), cache, time, ct));

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
    /// Runs one named set of PromQL (<see cref="ConsoleMetricQueries"/>). 501 when PROMETHEUS_URL isn't
    /// configured, 503 when Prometheus can't be reached. Cached like other polled reads.
    /// </summary>
    public static async Task<IResult> HandleMetricsAsync(
        string name, string? id, string? ids, string? range, IPrometheusClient? prometheus,
        ConsoleResponseCache cache, TimeProvider time, CancellationToken cancellationToken)
    {
        if (prometheus == null)
        {
            return Results.Problem(title: "Metrics not configured", detail: "Set PROMETHEUS_URL on the operator.",
                statusCode: StatusCodes.Status501NotImplemented);
        }

        range ??= "1h";
        if (!ConsoleMetricQueries.Ranges.TryGetValue(range, out var span))
        {
            return Results.BadRequest($"range must be one of: {string.Join(", ", ConsoleMetricQueries.Ranges.Keys)}");
        }

        var idList = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (idList.Length > ConsoleMetricQueries.MaxIds)
        {
            return Results.BadRequest($"At most {ConsoleMetricQueries.MaxIds} ids");
        }

        var step = TimeSpan.FromSeconds(Math.Max(15, span.TotalSeconds / ConsoleMetricQueries.PointsPerRange));
        var window = $"{(int)Math.Max(120, step.TotalSeconds)}s";
        var queries = ConsoleMetricQueries.Build(name, string.IsNullOrEmpty(id) ? null : id, idList, window);
        if (queries == null)
        {
            return Results.BadRequest($"Unknown metrics '{name}', or it needs an id");
        }

        try
        {
            var key = CacheKey($"metrics/{name}", [new("id", id ?? string.Empty), new("ids", ids ?? string.Empty), new("range", range)]);
            var reply = await cache.GetOrAddAsync(key, async () =>
            {
                var end = time.GetUtcNow();
                var results = await Task.WhenAll(queries.Select(async q =>
                {
                    var series = q.Instant
                        ? await prometheus.QueryAsync(q.PromQl, end, CancellationToken.None)
                        : await prometheus.QueryRangeAsync(q.PromQl, end - span, end, step, CancellationToken.None);
                    return series.Select(s => new MetricSeries { Name = q.Series, Labels = s.Labels, Points = s.Points });
                }));
                var response = new ConsoleMetricsResponse { Series = results.SelectMany(r => r).ToList() };
                return new WorkerReply(StatusCodes.Status200OK, JsonSerializer.Serialize(response, JsonOptions));
            }, cancellationToken);
            return ToResult(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(title: "Prometheus unavailable", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
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
