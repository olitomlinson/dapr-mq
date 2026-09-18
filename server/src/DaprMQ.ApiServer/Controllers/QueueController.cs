using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using DaprMQ.Interfaces;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.ApiServer.Controllers;

[ApiController]
[Route("queue")]
public class QueueController : ControllerBase
{
    private readonly ILogger<QueueController> _logger;
    private readonly IQueueActorInvoker _actorInvoker;
    private readonly IHttpSinkActorInvoker _httpSinkActorInvoker;
    private readonly Dapr.Actors.Client.IActorProxyFactory _actorProxyFactory;
    private readonly IObjectStore _objectStore;
    private readonly IObjectClaimTokenIssuer _objectClaimTokenIssuer;
    private readonly IBlobReaperActorInvoker _blobReaperActorInvoker;
    private readonly BlobReapConfig _blobReapConfig;
    private readonly ISessionCoordinatorActorInvoker _sessionCoordinatorActorInvoker;

    private const int MaxSessionIdLength = 256;

    public QueueController(
        ILogger<QueueController> logger,
        IQueueActorInvoker actorInvoker,
        IHttpSinkActorInvoker httpSinkActorInvoker,
        Dapr.Actors.Client.IActorProxyFactory actorProxyFactory,
        IObjectStore objectStore,
        IObjectClaimTokenIssuer objectClaimTokenIssuer,
        IBlobReaperActorInvoker blobReaperActorInvoker,
        BlobReapConfig blobReapConfig,
        ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker)
    {
        _logger = logger;
        _actorInvoker = actorInvoker;
        _httpSinkActorInvoker = httpSinkActorInvoker;
        _actorProxyFactory = actorProxyFactory;
        _objectStore = objectStore;
        _objectClaimTokenIssuer = objectClaimTokenIssuer;
        _blobReaperActorInvoker = blobReaperActorInvoker;
        _blobReapConfig = blobReapConfig;
        _sessionCoordinatorActorInvoker = sessionCoordinatorActorInvoker;
    }

    /// <summary>
    /// Validates an optional sessionId's format (controller-level, per CLAUDE.md's
    /// controller-validates-format / actor-validates-business-logic split) - non-empty when
    /// provided, and bounded since it's folded directly into a derived actor id
    /// ("{queueId}-session-{sessionId}").
    /// </summary>
    private static bool IsValidSessionId(string? sessionId, out string? error)
    {
        if (sessionId == null)
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            error = "sessionId cannot be empty";
            return false;
        }

        if (sessionId.Length > MaxSessionIdLength)
        {
            error = $"sessionId cannot exceed {MaxSessionIdLength} characters";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Resolves the ActorId an Enqueue (or a session lease op) should target - the plain queue actor
    /// when sessionId is absent, or its dedicated per-session QueueActor instance otherwise. See
    /// plan §2.2 - routing is a controller-layer concern, never an actor-to-actor forward.
    /// </summary>
    private static string ResolveTargetActorId(string queueId, string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? queueId : $"{queueId}-session-{sessionId}";

    /// <summary>
    /// Enqueue items to the queue with optional priority per item.
    /// </summary>
    [HttpPost("{queueId}/enqueue")]
    public async Task<IActionResult> Enqueue(
        string queueId,
        [FromBody] ApiEnqueueRequest request)
    {
        try
        {
            // Validate items array
            if (request.Items == null || request.Items.Count == 0)
            {
                return BadRequest(new ApiErrorResponse("Items array cannot be empty"));
            }

            if (request.Items.Count > 10000)
            {
                return BadRequest(new ApiErrorResponse("Maximum 10000 items per enqueue"));
            }

            // Validate priorities and sessionId format
            foreach (var item in request.Items)
            {
                if (item.Priority < 0)
                {
                    return BadRequest(new ApiErrorResponse("Priority must be non-negative"));
                }

                if (!IsValidSessionId(item.SessionId, out var sessionError))
                {
                    return BadRequest(new ApiErrorResponse(sessionError!));
                }
            }

            _logger.LogDebug($"Enqueue request for queue {queueId} with {request.Items.Count} items");

            // Group by target actor (the plain queue, or a per-session QueueActor instance) so
            // each actor call stays a single batched Enqueue - a mixed-session batch costs one Enqueue
            // per distinct session, an ordinary (session-free) batch costs exactly what it always
            // did: one call (plan §2.2, invariant 2).
            var groups = request.Items.GroupBy(apiItem => ResolveTargetActorId(queueId, apiItem.SessionId));

            int totalEnqueued = 0;
            int totalDeduplicated = 0;
            foreach (var group in groups)
            {
                var actorItems = group.Select(apiItem => new EnqueueItem
                {
                    ItemJson = apiItem.Item.GetRawText(),
                    Priority = apiItem.Priority,
                    IdempotencyKey = apiItem.IdempotencyKey
                }).ToList();

                var result = await InvokeEnqueueAsync(group.Key, actorItems);
                if (!result.Success)
                {
                    return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to enqueue items"));
                }

                totalEnqueued += result.ItemsEnqueued;
                totalDeduplicated += result.ItemsDeduplicated;
            }

            return BuildEnqueueResponse(queueId, totalEnqueued, totalDeduplicated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error enqueuing items to queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Shared enqueue path used by both the JSON Enqueue endpoint and enqueue-object. Invokes the actor's
    /// Enqueue method and maps the result to the same HTTP response shape either caller expects.
    /// </summary>
    private async Task<IActionResult> EnqueueItemsAsync(string queueId, string targetActorId, List<EnqueueItem> items)
    {
        var result = await InvokeEnqueueAsync(targetActorId, items);

        if (!result.Success)
        {
            return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to enqueue items"));
        }

        return BuildEnqueueResponse(queueId, result.ItemsEnqueued, result.ItemsDeduplicated);
    }

    private async Task<EnqueueResponse> InvokeEnqueueAsync(string targetActorId, List<EnqueueItem> items)
    {
        return await _actorInvoker.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            new ActorId(targetActorId),
            ActorMethodNames.Enqueue,
            new EnqueueRequest { Items = items });
    }

    private static IActionResult BuildEnqueueResponse(string queueId, int itemsEnqueued, int itemsDeduplicated)
    {
        var message = itemsDeduplicated > 0
            ? $"Enqueued {itemsEnqueued} items to queue {queueId} ({itemsDeduplicated} deduplicated)"
            : $"Enqueued {itemsEnqueued} items to queue {queueId}";
        return new OkObjectResult(new ApiEnqueueResponse(true, message, itemsEnqueued, itemsDeduplicated));
    }

    /// <summary>
    /// Enqueue a single large object, streaming it directly into the configured object store and
    /// enqueuing only a small reference envelope to the actor.
    /// </summary>
    [HttpPost("{queueId}/enqueue-object")]
    public async Task<IActionResult> EnqueueObject(
        string queueId,
        [FromHeader(Name = "priority")] int priority = 1,
        [FromHeader(Name = "content-type")] string? contentType = null,
        [FromHeader(Name = "prefix")] string? prefix = null,
        [FromHeader(Name = "idempotency-key")] string? idempotencyKey = null,
        [FromHeader(Name = "session-id")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (priority < 0)
            {
                return BadRequest(new ApiErrorResponse("Priority must be non-negative"));
            }

            if (!IsValidSessionId(sessionId, out var sessionError))
            {
                return BadRequest(new ApiErrorResponse(sessionError!));
            }

            var effectivePrefix = string.IsNullOrWhiteSpace(prefix) ? queueId : prefix;
            var objectId = Guid.NewGuid().ToString("N");

            _logger.LogDebug($"EnqueueObject request for queue {queueId}, prefix {effectivePrefix}, objectId {objectId}");

            string blobReference;
            try
            {
                blobReference = await _objectStore.UploadAsync(effectivePrefix, objectId, Request.Body, contentType, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ApiErrorResponse(ex.Message));
            }

            var itemJson = BlobReferenceEnvelope.Build(blobReference, Request.ContentLength, contentType);

            var actorItems = new List<EnqueueItem>
            {
                new EnqueueItem
                {
                    ItemJson = itemJson,
                    Priority = priority,
                    IdempotencyKey = idempotencyKey
                }
            };

            return await EnqueueItemsAsync(queueId, ResolveTargetActorId(queueId, sessionId), actorItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error enqueuing object to queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Dequeue items from the queue with optional acknowledgement.
    /// </summary>
    [HttpPost("{queueId}/dequeue")]
    public async Task<IActionResult> Dequeue(
        string queueId,
        [FromHeader(Name = "require-ack")] bool require_ack = false,
        [FromHeader(Name = "ttl-seconds")] int ttl_seconds = 30,
        [FromHeader(Name = "allow-competing-consumers")] bool allow_competing_consumers = false,
        [FromHeader(Name = "count")] int count = 1,
        [FromHeader(Name = "lease-id")] string? lease_id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug($"Dequeue request for queue {queueId}, require_ack={require_ack}, allow_competing_consumers={allow_competing_consumers}, count={count}");

            // Validate count parameter
            if (count < 0 || count > 1000)
            {
                return BadRequest(new ApiErrorResponse("Count must be between 0 and 1000"));
            }

            var actorId = new ActorId(queueId);

            if (require_ack)
            {
                var result = await _actorInvoker.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                    actorId,
                    ActorMethodNames.DequeueLocked,
                    new DequeueLockedRequest
                    {
                        TtlSeconds = ttl_seconds,
                        Count = count,
                        AllowCompetingConsumers = allow_competing_consumers,
                        LeaseId = lease_id
                    });

                // Session-lease guard rejection (session-scoped queue actor, missing/invalid/expired lease-id).
                if (result.ErrorCode == "SESSION_LEASE_EXPIRED")
                {
                    return StatusCode(410, new ApiErrorResponse(result.Message ?? "Session lease expired"));
                }

                if (result.ErrorCode != null)
                {
                    return BadRequest(new ApiErrorResponse(result.Message ?? "Invalid session lease"));
                }

                // If locked by another operation (Locked=true but no items), return 423 Locked
                if (result.Locked && result.Items.Count == 0)
                {
                    return StatusCode(423, new ApiLockedResponse(
                        result.Message,
                        result.LockExpiresAt
                    ));
                }

                // If queue is empty, return 204 No Content
                if (result.IsEmpty)
                {
                    return NoContent();
                }

                // Return items array - blob items never stream inline; they carry an
                // objectClaimToken the caller redeems via GET /object/{token}.
                var apiItems = new List<ApiDequeueLockedItem>();
                foreach (var item in result.Items)
                {
                    apiItems.Add(new ApiDequeueLockedItem(
                        ResolveItemElement(item.ItemJson, item.ObjectClaimToken, item.BlobContentType),
                        item.Priority,
                        item.LockId,
                        item.LockExpiresAt));
                }

                return Ok(new ApiDequeueLockedResponse(apiItems, result.Locked, result.Message));
            }
            else
            {
                var result = await _actorInvoker.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                    actorId,
                    ActorMethodNames.Dequeue,
                    new DequeueRequest { Count = count, LeaseId = lease_id });

                // Session-lease guard rejection (session-scoped queue actor, missing/invalid/expired lease-id).
                if (result.ErrorCode == "SESSION_LEASE_EXPIRED")
                {
                    return StatusCode(410, new ApiErrorResponse(result.Message ?? "Session lease expired"));
                }

                if (result.ErrorCode != null)
                {
                    return BadRequest(new ApiErrorResponse(result.Message ?? "Invalid session lease"));
                }

                // If locked by another operation, return 423 Locked
                if (result.Locked)
                {
                    return StatusCode(423, new ApiLockedResponse(
                        result.Message,
                        result.LockExpiresAt
                    ));
                }

                // If queue is empty, return 204 No Content
                if (result.IsEmpty)
                {
                    return NoContent();
                }

                // Return items array - blob items never stream inline; they carry an
                // objectClaimToken the caller redeems via GET /object/{token}.
                var apiItems = new List<ApiDequeueItem>();
                foreach (var item in result.Items)
                {
                    apiItems.Add(new ApiDequeueItem(
                        ResolveItemElement(item.ItemJson, item.ObjectClaimToken, contentType: null),
                        item.Priority));
                }

                return Ok(new ApiDequeueResponse(apiItems));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error dequeuing item from queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Streams the dereferenced content of an offloaded blob directly as the HTTP response body.
    /// Used only by the standalone GET /object/{token} download endpoint - Dequeue never streams
    /// blob content inline. Sets a file download name so browsers save the response to disk
    /// instead of rendering viewable content types (PDF, images, etc.) inline in the tab.
    /// </summary>
    private async Task<IActionResult> StreamBlobResponseAsync(string blobReference, CancellationToken cancellationToken, string? contentType = null)
    {
        var stream = await _objectStore.DownloadAsync(blobReference, cancellationToken);
        var downloadName = BuildDownloadFileName(blobReference, contentType);
        return File(stream, contentType ?? "application/octet-stream", downloadName);
    }

    private static readonly Dictionary<string, string> ContentTypeToExtension = new()
    {
        ["application/pdf"] = ".pdf",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["text/plain"] = ".txt",
        ["text/csv"] = ".csv",
        ["application/json"] = ".json",
        ["application/zip"] = ".zip",
        ["video/mp4"] = ".mp4",
        ["audio/mpeg"] = ".mp3",
    };

    /// <summary>
    /// Derives a download file name from the blob reference's trailing object id, with an
    /// extension guessed from content type (no original file name is preserved at enqueue time).
    /// </summary>
    private static string BuildDownloadFileName(string blobReference, string? contentType)
    {
        var objectId = blobReference.Split('/').Last();
        var extension = contentType != null && ContentTypeToExtension.TryGetValue(contentType, out var ext) ? ext : string.Empty;
        return $"{objectId}{extension}";
    }

    /// <summary>
    /// For a normal inline item, parses ItemJson as-is. For an offloaded blob item, returns the
    /// objectClaimToken (and content type) instead of dereferencing content - the raw object-store
    /// reference is never exposed, and the caller must fetch bytes via GET /object/{token}.
    /// </summary>
    private static JsonElement ResolveItemElement(string itemJson, string? objectClaimToken, string? contentType)
    {
        if (objectClaimToken == null)
        {
            return JsonDocument.Parse(itemJson).RootElement;
        }

        return JsonSerializer.SerializeToElement(new { objectClaimToken, contentType });
    }

    /// <summary>
    /// Acknowledge dequeued items using lock ID.
    /// </summary>
    [HttpPost("{queueId}/acknowledge")]
    public async Task<IActionResult> Acknowledge(
        string queueId,
        [FromBody] ApiAcknowledgeRequest request,
        [FromHeader(Name = "lease-id")] string? lease_id = null)
    {
        try
        {
            _logger.LogDebug($"Acknowledge request for queue {queueId} with lock_id {request.LockId}");

            var actorId = new ActorId(queueId);

            var result = await _actorInvoker.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                actorId,
                ActorMethodNames.Acknowledge,
                new AcknowledgeRequest
                {
                    LockId = request.LockId,
                    LeaseId = lease_id
                });

            // Check for error codes
            if (!result.Success)
            {
                var response = new ApiAcknowledgeResponse(
                    result.Success,
                    result.Message,
                    ErrorCode: result.ErrorCode
                );

                // Return 410 Gone if lock expired or the session lease has expired
                if (result.ErrorCode == "LOCK_EXPIRED" || result.ErrorCode == "SESSION_LEASE_EXPIRED")
                {
                    return StatusCode(410, response);
                }

                // Return 404 if lock not found
                if (result.ErrorCode == "LOCK_NOT_FOUND")
                {
                    return NotFound(response);
                }

                // Return 400 for invalid lock_id
                if (result.ErrorCode == "INVALID_LOCK_ID")
                {
                    return BadRequest(response);
                }

                // Default to 400 for other failures
                return BadRequest(response);
            }

            return Ok(new ApiAcknowledgeResponse(
                result.Success,
                result.Message,
                result.ItemsAcknowledged
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error acknowledging items for queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Extend an existing lock by adding additional TTL seconds.
    /// </summary>
    [HttpPost("{queueId}/extend-lock")]
    public async Task<IActionResult> ExtendLock(
        string queueId,
        [FromBody] ApiExtendLockRequest request,
        [FromHeader(Name = "lease-id")] string? lease_id = null)
    {
        try
        {
            _logger.LogDebug($"ExtendLock request for queue {queueId} with lock_id {request.LockId}");

            var actorId = new ActorId(queueId);

            var result = await _actorInvoker.InvokeMethodAsync<ExtendLockRequest, ExtendLockResponse>(
                actorId,
                ActorMethodNames.ExtendLock,
                new ExtendLockRequest
                {
                    LockId = request.LockId,
                    AdditionalTtlSeconds = request.AdditionalTtlSeconds,
                    LeaseId = lease_id
                });

            // Check for error codes
            if (!result.Success)
            {
                var errorResponse = new ApiErrorResponse(result.ErrorMessage ?? "Failed to extend lock");

                // Return 410 Gone if lock expired or the session lease has expired
                if (result.ErrorCode == "LOCK_EXPIRED" || result.ErrorCode == "SESSION_LEASE_EXPIRED")
                {
                    return StatusCode(410, errorResponse);
                }

                // Return 404 if lock not found
                if (result.ErrorCode == "LOCK_NOT_FOUND")
                {
                    return NotFound(errorResponse);
                }

                // Return 400 for invalid lock_id or TTL
                if (result.ErrorCode == "INVALID_LOCK_ID" || result.ErrorCode == "INVALID_TTL")
                {
                    return BadRequest(errorResponse);
                }

                // Default to 400 for other failures
                return BadRequest(errorResponse);
            }

            return Ok(new ApiExtendLockResponse(
                NewExpiresAt: (long)result.NewExpiresAt,
                LockId: request.LockId
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extending lock for queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }


    /// <summary>
    /// Extend an existing lock by adding additional TTL seconds.
    /// </summary>
    [HttpPost("{queueId}/test-unsafe-unload")]
    public async Task<IActionResult> TestUnsafeUnload(
        string queueId)
    {

        var actorId = new ActorId(queueId);

        await _actorInvoker.InvokeMethodAsync<UnsafeUnloadRequest>(
            actorId,
            ActorMethodNames.TestUnsafeUnload,
            new UnsafeUnloadRequest
            {
                Lol = "123"
            });


        return Ok();

    }

    /// <summary>
    /// Move a locked item to the dead letter queue and void the lock.
    /// </summary>
    [HttpPost("{queueId}/deadletter")]
    public async Task<IActionResult> DeadLetter(
        string queueId,
        [FromBody] ApiDeadLetterRequest request,
        [FromHeader(Name = "lease-id")] string? lease_id = null)
    {
        try
        {
            _logger.LogDebug($"DeadLetter request for queue {queueId} with lock_id {request.LockId}");

            var actorId = new ActorId(queueId);

            var result = await _actorInvoker.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                actorId,
                ActorMethodNames.DeadLetter,
                new DeadLetterRequest
                {
                    LockId = request.LockId,
                    LeaseId = lease_id
                });

            // Check for error status
            if (result.Status == "ERROR")
            {
                var response = new ApiDeadLetterResponse(
                    false,
                    result.Message ?? "Failed to move item to dead letter queue",
                    ErrorCode: result.ErrorCode
                );

                // Return 410 Gone if lock expired or the session lease has expired
                if (result.ErrorCode == "LOCK_EXPIRED" || result.ErrorCode == "SESSION_LEASE_EXPIRED")
                {
                    return StatusCode(410, response);
                }

                // Return 404 if lock not found
                if (result.ErrorCode == "LOCK_NOT_FOUND")
                {
                    return NotFound(response);
                }

                // Return 400 for invalid lock_id
                if (result.ErrorCode == "INVALID_LOCK_ID")
                {
                    return BadRequest(response);
                }

                // Default to 400 for other failures
                return BadRequest(response);
            }

            return Ok(new ApiDeadLetterResponse(
                true,
                result.Message ?? "Item moved to dead letter queue",
                DlqId: result.DlqId
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error moving item to dead letter queue for {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Fetch the content of an offloaded large object by ObjectClaimToken. The token is
    /// self-contained (blob reference + content type + expiry) and validated standalone - no
    /// queue or lock lookup is involved. On success, postpones the blob's scheduled reap so the
    /// caller has a fresh window to have actually retrieved the data.
    /// </summary>
    [HttpGet("/object/{token}")]
    public async Task<IActionResult> GetObject(string token, CancellationToken cancellationToken)
    {
        try
        {
            if (!_objectClaimTokenIssuer.TryResolve(token, out var claim, out var isExpired))
            {
                if (isExpired)
                {
                    return StatusCode(410, new ApiErrorResponse("Object claim token has expired"));
                }

                return BadRequest(new ApiErrorResponse("Invalid object claim token"));
            }

            var result = await StreamBlobResponseAsync(claim!.BlobReference, cancellationToken, contentType: claim.ContentType);

            try
            {
                await _blobReaperActorInvoker.InvokeMethodAsync<PostponeDeletionRequest>(
                    new ActorId(HashBlobReference(claim.BlobReference)),
                    "PostponeDeletion",
                    new PostponeDeletionRequest { BlobReference = claim.BlobReference, NewDelaySeconds = _blobReapConfig.PostDownloadSeconds },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to postpone blob reap after download for {BlobReference}", claim.BlobReference);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching object for token");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Derives the same slash-free reaper actor id QueueActor uses when scheduling deletion (see
    /// QueueActor.HashBlobReference), so PostponeDeletion targets the same actor instance.
    /// </summary>
    private static string HashBlobReference(string blobReference)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(blobReference));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Register a sink for pull-based message delivery to an HTTP endpoint.
    /// </summary>
    [HttpPost("{queueId}/sink/http/register")]
    public async Task<IActionResult> RegisterSink(
        string queueId,
        [FromBody] ApiRegisterHttpSinkRequest request)
    {
        try
        {
            // Validate URL
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return BadRequest(new ApiRegisterHttpSinkResponse(
                    false,
                    "URL cannot be empty"
                ));
            }

            // Validate URL format
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            {
                return BadRequest(new ApiRegisterHttpSinkResponse(
                    false,
                    "URL must be a valid absolute URI"
                ));
            }

            // Validate MaxConcurrency
            if (request.MaxConcurrency < 1 || request.MaxConcurrency > 100)
            {
                return BadRequest(new ApiRegisterHttpSinkResponse(
                    false,
                    "MaxConcurrency must be between 1 and 100"
                ));
            }

            // Validate LockTtlSeconds
            if (request.LockTtlSeconds < 1 || request.LockTtlSeconds > 300)
            {
                return BadRequest(new ApiRegisterHttpSinkResponse(
                    false,
                    "LockTtlSeconds must be between 1 and 300"
                ));
            }

            // Calculate sink actor ID
            string sinkActorId = $"{queueId}-sink";
            var sinkActorId_ActorId = new ActorId(sinkActorId);

            // Build InitializeHttpSinkRequest (dynamic polling starts at 1s)
            var initRequest = new InitializeHttpSinkRequest
            {
                Url = request.Url,
                QueueActorId = queueId,
                MaxConcurrency = request.MaxConcurrency,
                LockTtlSeconds = request.LockTtlSeconds
            };

            // Initialize HttpSinkActor (registers reminder, starts polling)
            await _httpSinkActorInvoker.InvokeMethodAsync(
                sinkActorId_ActorId,
                ActorMethodNames.InitializeHttpSink,
                initRequest);

            return Ok(new ApiRegisterHttpSinkResponse(
                true,
                "Sink registered successfully",
                sinkActorId
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error registering sink for {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Unregister the sink for the specified queue.
    /// </summary>
    [HttpPost("{queueId}/sink/http/unregister")]
    public async Task<IActionResult> UnregisterSink(string queueId)
    {
        try
        {
            // Calculate sink actor ID
            string sinkActorId = $"{queueId}-sink";
            var sinkActorId_ActorId = new ActorId(sinkActorId);

            // Uninitialize HttpSinkActor (unregisters reminder)
            await _httpSinkActorInvoker.InvokeMethodAsync(
                sinkActorId_ActorId,
                ActorMethodNames.UninitializeHttpSink);

            return Ok(new ApiUnregisterHttpSinkResponse(
                true,
                "Sink unregistered successfully"
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error unregistering sink for {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Claim exclusive ownership of a session - "any available" (sessionId omitted) or targeted
    /// (sessionId provided, sticky routing). On success, dequeue/acknowledge/extend-lock/deadletter
    /// are then called against "{queueId}-session-{sessionId}" with a lease-id header.
    /// </summary>
    [HttpPost("{queueId}/sessions/accept")]
    public async Task<IActionResult> AcceptSession(string queueId, [FromBody] ApiAcceptSessionRequest? request)
    {
        try
        {
            var sessionId = request?.SessionId;
            if (!IsValidSessionId(sessionId, out var sessionError))
            {
                return BadRequest(new ApiErrorResponse(sessionError!));
            }

            var leaseSeconds = request?.LeaseSeconds ?? 30;
            if (leaseSeconds < 1 || leaseSeconds > 300)
            {
                return BadRequest(new ApiErrorResponse("leaseSeconds must be between 1 and 300"));
            }

            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                new ActorId(queueId),
                ActorMethodNames.AcceptSession,
                new AcceptSessionRequest { SessionId = sessionId, LeaseSeconds = leaseSeconds });

            if (!result.Success)
            {
                var response = new ApiErrorResponse(result.ErrorMessage ?? "Failed to accept session");

                return result.ErrorCode switch
                {
                    "SESSION_NOT_FOUND" => NotFound(response),
                    "SESSION_LOCKED" => StatusCode(423, response),
                    "NO_SESSIONS_AVAILABLE" => NoContent(),
                    "SESSION_ACTOR_UNAVAILABLE" => StatusCode(502, response),
                    _ => BadRequest(response)
                };
            }

            return Ok(new ApiAcceptSessionResponse(result.SessionId!, result.LeaseId!, result.LeaseExpiresAt!.Value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error accepting session for queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Heartbeat to keep an already-claimed session lease alive.
    /// </summary>
    [HttpPost("{queueId}/sessions/{sessionId}/renew")]
    public async Task<IActionResult> RenewSessionLease(string queueId, string sessionId, [FromBody] ApiRenewSessionLeaseRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.LeaseId))
            {
                return BadRequest(new ApiErrorResponse("leaseId cannot be empty"));
            }

            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<RenewSessionLeaseRequest, RenewSessionLeaseResponse>(
                new ActorId(queueId),
                ActorMethodNames.RenewSessionLease,
                new RenewSessionLeaseRequest { SessionId = sessionId, LeaseId = request.LeaseId, AdditionalSeconds = request.AdditionalSeconds });

            if (!result.Success)
            {
                var response = new ApiErrorResponse(result.ErrorMessage ?? "Failed to renew session lease");

                return result.ErrorCode switch
                {
                    "SESSION_LEASE_EXPIRED" => StatusCode(410, response),
                    _ => BadRequest(response)
                };
            }

            return Ok(new ApiRenewSessionLeaseResponse(result.NewExpiresAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error renewing session lease for queue {queueId}, session {sessionId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Explicitly give up a claimed session lease, freeing it for another consumer immediately
    /// rather than waiting out the lease TTL. Idempotent - releasing an already-released/unknown
    /// session succeeds.
    /// </summary>
    [HttpPost("{queueId}/sessions/{sessionId}/release")]
    public async Task<IActionResult> ReleaseSession(string queueId, string sessionId, [FromBody] ApiReleaseSessionRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.LeaseId))
            {
                return BadRequest(new ApiErrorResponse("leaseId cannot be empty"));
            }

            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ReleaseSessionRequest, ReleaseSessionResponse>(
                new ActorId(queueId),
                ActorMethodNames.ReleaseSession,
                new ReleaseSessionRequest { SessionId = sessionId, LeaseId = request.LeaseId });

            if (!result.Success)
            {
                return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to release session"));
            }

            return Ok(new ApiReleaseSessionResponse(true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error releasing session for queue {queueId}, session {sessionId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

}
