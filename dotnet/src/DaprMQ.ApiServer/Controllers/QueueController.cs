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
    private readonly IActorInvoker _actorInvoker;

    public QueueController(
        ILogger<QueueController> logger,
        IActorInvoker actorInvoker)
    {
        _logger = logger;
        _actorInvoker = actorInvoker;
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

            var actorId = new ActorId(queueId);

            // Convert API items to actor items
            var actorItems = request.Items.Select(apiItem => new PushItem
            {
                ItemJson = apiItem.Item.GetRawText(),
                Priority = apiItem.Priority
            }).ToList();

            var result = await _actorInvoker.InvokeMethodAsync<PushRequest, PushResponse>(
                actorId,
                ActorMethodNames.Push,
                new PushRequest
                {
                    Items = actorItems
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
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pushing items to queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Pop items from the queue with optional acknowledgement.
    /// </summary>
    [HttpPost("{queueId}/pop")]
    public async Task<IActionResult> Pop(
        string queueId,
        [FromHeader] bool require_ack = false,
        [FromHeader] int ttl_seconds = 30)
    {
        try
        {
            _logger.LogDebug($"Pop request for queue {queueId}, require_ack={require_ack}");

            var actorId = new ActorId(queueId);

            if (require_ack)
            {
                var result = await _actorInvoker.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                    actorId,
                    ActorMethodNames.PopWithAck,
                    new PopWithAckRequest
                    {
                        TtlSeconds = ttl_seconds
                    });

                // If locked by another operation (Locked=true but no LockId), return 423 Locked
                if (result.Locked && result.LockId == null)
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


                return Ok(new ApiPopWithAckResponse(
                JsonDocument.Parse(result.ItemJson).RootElement,
                result.Locked,
                result.LockId,
                result.LockExpiresAt,
                result.Message,
                result.Priority));

            }
            else
            {
                var result = await _actorInvoker.InvokeMethodAsync<PopResponse>(
                    actorId,
                    ActorMethodNames.Pop);

                // DEBUG: Log what we actually received
                _logger.LogWarning($"[DEBUG] Pop result - ItemJson: '{result.ItemJson ?? "NULL"}', Locked: {result.Locked}, IsEmpty: {result.IsEmpty}, Message: '{result.Message ?? "NULL"}'");

                // If locked by another operation, return 423 Locked
                if (result.Locked)
                {
                    _logger.LogWarning("[DEBUG] Returning 423 Locked");
                    return StatusCode(423, new ApiLockedResponse(
                        result.Message,
                        result.LockExpiresAt
                    ));
                }

                // If queue is empty, return 204 No Content
                // Check both IsEmpty flag and null ItemJson for backwards compatibility
                if (result.IsEmpty)
                {
                    return NoContent();
                }

                return Ok(new ApiPopResponse(JsonDocument.Parse(result.ItemJson).RootElement, result.Priority));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error popping item from queue {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
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
                DlqActorId: result.DlqActorId
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error moving item to dead letter queue for {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }
}
