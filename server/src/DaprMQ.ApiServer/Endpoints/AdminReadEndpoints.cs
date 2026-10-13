using System.Text.RegularExpressions;
using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DaprMQ.ApiServer.Endpoints;

/// <summary>
/// Internal, worker-only, read-only admin reads (GET /internal/admin/*) behind the admin console.
/// Called by DaprMQ.Operator via Dapr service invocation, for the same reason as
/// <see cref="QueueDepthEndpoint"/>: only workers can read actor state. Reads go through the
/// actor-state API and never call or activate an actor.
/// </summary>
public static class AdminReadEndpoints
{
    public const string CatalogRoute = "/internal/admin/catalog";
    public const int DefaultCatalogLimit = 100;
    public const int MaxCatalogLimit = 1000;

    /// <summary>Default and maximum page size for message, lock and session browsing.</summary>
    public const int DefaultBrowseLimit = 50;
    public const int MaxBrowseLimit = 100;

    private const string Prefix = "/internal/admin";

    public static IEndpointRouteBuilder MapAdminReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(CatalogRoute, (string? kind, string? cursor, int? limit, ICatalogReadService service, CancellationToken ct) =>
            HandleCatalogAsync(kind, cursor, limit, service, ct));

        endpoints.MapGet($"{Prefix}/queues/{{queueId}}", (string queueId, IAdminReadService service, CancellationToken ct) =>
            HandleQueueAsync(queueId, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/messages",
            (string queueId, int? priority, string? cursor, int? limit, IAdminReadService service, CancellationToken ct) =>
                HandleMessagesAsync(queueId, priority, cursor, limit, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/messages/{{location}}",
            (string queueId, string location, long? sequence, IAdminReadService service, CancellationToken ct) =>
                HandleMessageAsync(queueId, location, sequence, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/messages/{{location}}/payload",
            (string queueId, string location, long? sequence, IAdminReadService service, CancellationToken ct) =>
                HandleMessagePayloadAsync(queueId, location, sequence, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/locks",
            (string queueId, string? cursor, int? limit, IAdminReadService service, CancellationToken ct) =>
                HandleLocksAsync(queueId, cursor, limit, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/locks/{{lockId}}",
            (string queueId, string lockId, IAdminReadService service, CancellationToken ct) =>
                HandleLockAsync(queueId, lockId, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/locks/{{lockId}}/payload",
            (string queueId, string lockId, IAdminReadService service, CancellationToken ct) =>
                HandleLockPayloadAsync(queueId, lockId, service, ct));
        endpoints.MapGet($"{Prefix}/queues/{{queueId}}/sessions",
            (string queueId, string? cursor, int? limit, IAdminReadService service, CancellationToken ct) =>
                HandleSessionsAsync(queueId, cursor, limit, service, ct));
        endpoints.MapGet($"{Prefix}/topics/{{topicId}}", (string topicId, IAdminReadService service, CancellationToken ct) =>
            HandleTopicAsync(topicId, service, ct));
        return endpoints;
    }

    public static async Task<Results<Ok<CatalogPage>, BadRequest<string>>> HandleCatalogAsync(
        string? kind, string? cursor, int? limit, ICatalogReadService service, CancellationToken cancellationToken)
    {
        CatalogEntryKind? parsedKind = null;
        if (!string.IsNullOrEmpty(kind))
        {
            // Names only - Enum.TryParse would also accept "1".
            var name = Enum.GetNames<CatalogEntryKind>().FirstOrDefault(n => n.Equals(kind, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return TypedResults.BadRequest($"kind must be one of: {string.Join(", ", Enum.GetNames<CatalogEntryKind>())}");
            }
            parsedKind = Enum.Parse<CatalogEntryKind>(name);
        }

        var pageSize = limit ?? DefaultCatalogLimit;
        if (pageSize is < 1 or > MaxCatalogLimit)
        {
            return TypedResults.BadRequest($"limit must be between 1 and {MaxCatalogLimit}");
        }

        return TypedResults.Ok(await service.ListAsync(parsedKind, cursor, pageSize, cancellationToken));
    }

    public static async Task<Results<Ok<QueueDetail>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleQueueAsync(
        string queueId, IAdminReadService service, CancellationToken cancellationToken) =>
        ToResult(await service.GetQueueAsync(queueId, cancellationToken));

    public static async Task<Results<Ok<AdminMessagePage>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleMessagesAsync(
        string queueId, int? priority, string? cursor, int? limit, IAdminReadService service, CancellationToken cancellationToken)
    {
        if (priority is < 0)
        {
            return TypedResults.BadRequest("priority must not be negative");
        }

        MessageCursor? parsedCursor = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!MessageCursor.TryParse(cursor, out var c))
            {
                return TypedResults.BadRequest("cursor is malformed");
            }
            parsedCursor = c;
        }

        if (!TryBrowseLimit(limit, out var pageSize, out var error))
        {
            return error;
        }

        return ToResult(await service.ListMessagesAsync(queueId, priority, parsedCursor, pageSize, cancellationToken));
    }

    public static async Task<Results<Ok<AdminMessage>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleMessageAsync(
        string queueId, string location, long? sequence, IAdminReadService service, CancellationToken cancellationToken)
    {
        if (!TryMessageAddress(location, sequence, out var parsed, out var error))
        {
            return TypedResults.BadRequest(error);
        }

        return ToResult(await service.GetMessageAsync(queueId, parsed, sequence!.Value, cancellationToken));
    }

    public static async Task<Results<Ok<AdminPayload>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleMessagePayloadAsync(
        string queueId, string location, long? sequence, IAdminReadService service, CancellationToken cancellationToken)
    {
        if (!TryMessageAddress(location, sequence, out var parsed, out var error))
        {
            return TypedResults.BadRequest(error);
        }

        return ToResult(await service.GetMessagePayloadAsync(queueId, parsed, sequence!.Value, cancellationToken));
    }

    public static async Task<Results<Ok<AdminLockPage>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleLocksAsync(
        string queueId, string? cursor, int? limit, IAdminReadService service, CancellationToken cancellationToken)
    {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor) && (!int.TryParse(cursor, System.Globalization.NumberStyles.None, null, out offset)))
        {
            return TypedResults.BadRequest("cursor is malformed");
        }

        if (!TryBrowseLimit(limit, out var pageSize, out var error))
        {
            return error;
        }

        return ToResult(await service.ListLocksAsync(queueId, offset, pageSize, cancellationToken));
    }

    public static async Task<Results<Ok<AdminLock>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleLockAsync(
        string queueId, string lockId, IAdminReadService service, CancellationToken cancellationToken)
    {
        if (!LockIdFormat.IsMatch(lockId))
        {
            return TypedResults.BadRequest("lockId is malformed");
        }

        return ToResult(await service.GetLockAsync(queueId, lockId, cancellationToken));
    }

    public static async Task<Results<Ok<AdminPayload>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleLockPayloadAsync(
        string queueId, string lockId, IAdminReadService service, CancellationToken cancellationToken)
    {
        if (!LockIdFormat.IsMatch(lockId))
        {
            return TypedResults.BadRequest("lockId is malformed");
        }

        return ToResult(await service.GetLockPayloadAsync(queueId, lockId, cancellationToken));
    }

    public static async Task<Results<Ok<AdminSessionPage>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleSessionsAsync(
        string queueId, string? cursor, int? limit, IAdminReadService service, CancellationToken cancellationToken)
    {
        if (!TryBrowseLimit(limit, out var pageSize, out var error))
        {
            return error;
        }

        return ToResult(await service.ListSessionsAsync(queueId, string.IsNullOrEmpty(cursor) ? null : cursor, pageSize, cancellationToken));
    }

    public static async Task<Results<Ok<TopicDetail>, NotFound, StatusCodeHttpResult, BadRequest<string>>> HandleTopicAsync(
        string topicId, IAdminReadService service, CancellationToken cancellationToken) =>
        ToResult(await service.GetTopicAsync(topicId, cancellationToken));

    // Lock ids are 11 alphanumerics (QueueActor.GenerateLockId). Checked here because the id becomes
    // part of a state key in the actor-state URL.
    private static readonly Regex LockIdFormat = new("^[A-Za-z0-9]{1,64}$", RegexOptions.Compiled);

    private static bool TryMessageAddress(string location, long? sequence, out MessageLocation parsed, out string error)
    {
        error = string.Empty;
        if (!MessageLocation.TryParse(location, out parsed))
        {
            error = "location must be {priority}:{segment}:{index}";
            return false;
        }

        if (sequence is null)
        {
            error = "sequence is required";
            return false;
        }

        return true;
    }

    private static bool TryBrowseLimit(int? limit, out int pageSize, out BadRequest<string> error)
    {
        pageSize = limit ?? DefaultBrowseLimit;
        error = TypedResults.BadRequest($"limit must be between 1 and {MaxBrowseLimit}");
        return pageSize is >= 1 and <= MaxBrowseLimit;
    }

    private static Results<Ok<T>, NotFound, StatusCodeHttpResult, BadRequest<string>> ToResult<T>(AdminReadResult<T> result) =>
        result.Status switch
        {
            AdminReadStatus.Ok => TypedResults.Ok(result.Value!),
            AdminReadStatus.Gone => TypedResults.StatusCode(StatusCodes.Status410Gone),
            _ => TypedResults.NotFound()
        };
}
