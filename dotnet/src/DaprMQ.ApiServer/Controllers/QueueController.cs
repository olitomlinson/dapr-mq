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

    public QueueController(
        ILogger<QueueController> logger,
        IQueueActorInvoker actorInvoker,
        IHttpSinkActorInvoker httpSinkActorInvoker,
        Dapr.Actors.Client.IActorProxyFactory actorProxyFactory,
        IObjectStore objectStore,
        IObjectClaimTokenIssuer objectClaimTokenIssuer,
        IBlobReaperActorInvoker blobReaperActorInvoker,
        BlobReapConfig blobReapConfig)
    {
        _logger = logger;
        _actorInvoker = actorInvoker;
        _httpSinkActorInvoker = httpSinkActorInvoker;
        _actorProxyFactory = actorProxyFactory;
        _objectStore = objectStore;
        _objectClaimTokenIssuer = objectClaimTokenIssuer;
        _blobReaperActorInvoker = blobReaperActorInvoker;
        _blobReapConfig = blobReapConfig;
    }

    /// <summary>
    /// Push items to the queue with optional priority per item.
    /// </summary>
    [HttpPost("{queueId}/push")]
    public async Task<IActionResult> Push(
        string queueId,
        [FromBody] ApiPushRequest request)
    {
        try
        {
            // Validate items array
            if (request.Items == null || request.Items.Count == 0)
            {
                return BadRequest(new ApiErrorResponse("Items array cannot be empty"));
            }

            if (request.Items.Count > 1000)
            {
                return BadRequest(new ApiErrorResponse("Maximum 1000 items per push"));
            }

            // Validate priorities
            foreach (var item in request.Items)
            {
                if (item.Priority < 0)
                {
                    return BadRequest(new ApiErrorResponse("Priority must be non-negative"));
                }
            }

            _logger.LogDebug($"Push request for queue {queueId} with {request.Items.Count} items");

            // Convert API items to actor items
            var actorItems = request.Items.Select(apiItem => new PushItem
            {
                ItemJson = apiItem.Item.GetRawText(),
                Priority = apiItem.Priority,
                Sink = apiItem.Sink != null ? new SinkConfig
                {
                    DaprPubSub = apiItem.Sink.DaprPubSub != null ? new DaprPubSubSinkConfig
                    {
                        Metadata = apiItem.Sink.DaprPubSub.Metadata
                    } : null
                } : null
            }).ToList();

            return await PushItemsAsync(queueId, actorItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pushing items to queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Shared push path used by both the JSON Push endpoint and push-object. Invokes the actor's
    /// Push method and maps the result to the same HTTP response shape either caller expects.
    /// </summary>
    private async Task<IActionResult> PushItemsAsync(string queueId, List<PushItem> items)
    {
        var actorId = new ActorId(queueId);

        var result = await _actorInvoker.InvokeMethodAsync<PushRequest, PushResponse>(
            actorId,
            ActorMethodNames.Push,
            new PushRequest
            {
                Items = items
            });

        if (result.Success)
        {
            return Ok(new ApiPushResponse(
                true,
                $"Pushed {result.ItemsPushed} items to queue {queueId}",
                result.ItemsPushed
            ));
        }

        return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to push items"));
    }

    /// <summary>
    /// Push a single large object, streaming it directly into the configured object store and
    /// pushing only a small reference envelope to the actor.
    /// </summary>
    [HttpPost("{queueId}/push-object")]
    public async Task<IActionResult> PushObject(
        string queueId,
        [FromHeader(Name = "priority")] int priority = 1,
        [FromHeader(Name = "content-type")] string? contentType = null,
        [FromHeader(Name = "prefix")] string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (priority < 0)
            {
                return BadRequest(new ApiErrorResponse("Priority must be non-negative"));
            }

            var effectivePrefix = string.IsNullOrWhiteSpace(prefix) ? queueId : prefix;
            var objectId = Guid.NewGuid().ToString("N");

            _logger.LogDebug($"PushObject request for queue {queueId}, prefix {effectivePrefix}, objectId {objectId}");

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

            var actorItems = new List<PushItem>
            {
                new PushItem
                {
                    ItemJson = itemJson,
                    Priority = priority
                }
            };

            return await PushItemsAsync(queueId, actorItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pushing object to queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Pop items from the queue with optional acknowledgement.
    /// </summary>
    [HttpPost("{queueId}/pop")]
    public async Task<IActionResult> Pop(
        string queueId,
        [FromHeader(Name = "require-ack")] bool require_ack = false,
        [FromHeader(Name = "ttl-seconds")] int ttl_seconds = 30,
        [FromHeader(Name = "allow-competing-consumers")] bool allow_competing_consumers = false,
        [FromHeader(Name = "count")] int count = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug($"Pop request for queue {queueId}, require_ack={require_ack}, allow_competing_consumers={allow_competing_consumers}, count={count}");

            // Validate count parameter
            if (count < 0 || count > 100)
            {
                return BadRequest(new ApiErrorResponse("Count must be between 0 and 100"));
            }

            var actorId = new ActorId(queueId);

            if (require_ack)
            {
                var result = await _actorInvoker.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                    actorId,
                    ActorMethodNames.PopWithAck,
                    new PopWithAckRequest
                    {
                        TtlSeconds = ttl_seconds,
                        Count = count,
                        AllowCompetingConsumers = allow_competing_consumers
                    });

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
                var apiItems = new List<ApiPopWithAckItem>();
                foreach (var item in result.Items)
                {
                    apiItems.Add(new ApiPopWithAckItem(
                        ResolveItemElement(item.ItemJson, item.ObjectClaimToken, item.BlobContentType),
                        item.Priority,
                        item.LockId,
                        item.LockExpiresAt,
                        item.Sink != null ? new ApiSinkConfig(
                            item.Sink.DaprPubSub != null ? new ApiDaprPubSubSinkConfig(
                                item.Sink.DaprPubSub.Metadata
                            ) : null
                        ) : null));
                }

                return Ok(new ApiPopWithAckResponse(apiItems, result.Locked, result.Message));
            }
            else
            {
                var result = await _actorInvoker.InvokeMethodAsync<PopRequest, PopResponse>(
                    actorId,
                    ActorMethodNames.Pop,
                    new PopRequest { Count = count });

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
                var apiItems = new List<ApiPopItem>();
                foreach (var item in result.Items)
                {
                    apiItems.Add(new ApiPopItem(
                        ResolveItemElement(item.ItemJson, item.ObjectClaimToken, contentType: null),
                        item.Priority));
                }

                return Ok(new ApiPopResponse(apiItems));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error popping item from queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Streams the dereferenced content of an offloaded blob directly as the HTTP response body.
    /// Used only by the standalone GET /object/{token} download endpoint - Pop never streams
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
    /// extension guessed from content type (no original file name is preserved at push time).
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
    /// Acknowledge popped items using lock ID.
    /// </summary>
    [HttpPost("{queueId}/acknowledge")]
    public async Task<IActionResult> Acknowledge(
        string queueId,
        [FromBody] ApiAcknowledgeRequest request)
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
                    LockId = request.LockId
                });

            // Check for error codes
            if (!result.Success)
            {
                var response = new ApiAcknowledgeResponse(
                    result.Success,
                    result.Message,
                    ErrorCode: result.ErrorCode
                );

                // Return 410 Gone if lock expired
                if (result.ErrorCode == "LOCK_EXPIRED")
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
        [FromBody] ApiExtendLockRequest request)
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
                    AdditionalTtlSeconds = request.AdditionalTtlSeconds
                });

            // Check for error codes
            if (!result.Success)
            {
                var errorResponse = new ApiErrorResponse(result.ErrorMessage ?? "Failed to extend lock");

                // Return 410 Gone if lock expired
                if (result.ErrorCode == "LOCK_EXPIRED")
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
        [FromBody] ApiDeadLetterRequest request)
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
                    LockId = request.LockId
                });

            // Check for error status
            if (result.Status == "ERROR")
            {
                var response = new ApiDeadLetterResponse(
                    false,
                    result.Message ?? "Failed to move item to dead letter queue",
                    ErrorCode: result.ErrorCode
                );

                // Return 410 Gone if lock expired
                if (result.ErrorCode == "LOCK_EXPIRED")
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

}
