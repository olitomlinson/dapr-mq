using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using DaprMQ.Interfaces;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.ApiServer.Controllers;

[ApiController]
[Route("topic")]
public class TopicController : ControllerBase
{
    private readonly ILogger<TopicController> _logger;
    private readonly ITopicActorInvoker _actorInvoker;

    public TopicController(ILogger<TopicController> logger, ITopicActorInvoker actorInvoker)
    {
        _logger = logger;
        _actorInvoker = actorInvoker;
    }

    /// <summary>
    /// Publish items to a topic. Async accept - relay to subscribers happens out of band.
    /// </summary>
    [HttpPost("{topicId}/publish")]
    public async Task<IActionResult> Publish(string topicId, [FromBody] ApiPublishRequest request)
    {
        try
        {
            if (request.Items == null || request.Items.Count == 0)
            {
                return BadRequest(new ApiErrorResponse("Items array cannot be empty"));
            }

            var actorItems = request.Items.Select(apiItem => new EnqueueItem
            {
                ItemJson = apiItem.Item.GetRawText(),
                Priority = apiItem.Priority,
                IdempotencyKey = apiItem.IdempotencyKey
            }).ToList();

            var result = await _actorInvoker.InvokeMethodAsync<PublishRequest, PublishResponse>(
                new ActorId(topicId),
                ActorMethodNames.Publish,
                new PublishRequest { Items = actorItems });

            if (!result.Accepted)
            {
                return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to publish"));
            }

            return StatusCode(202, new ApiPublishResponse(result.Accepted, result.PublishId, result.Sequence));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error publishing to topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Register a subscriber on a topic. The subscriber is provisioned its own QueueActor,
    /// reachable via the returned QueueActorId using the existing queue Dequeue/DequeueLocked API.
    /// </summary>
    [HttpPost("{topicId}/subscribers/{subscriberId}")]
    public async Task<IActionResult> Subscribe(string topicId, string subscriberId, [FromBody] ApiSubscribeRequest? request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(subscriberId))
            {
                return BadRequest(new ApiErrorResponse("subscriberId cannot be empty"));
            }

            TopicHttpSinkConfig? httpSinkConfig = null;
            if (request?.HttpSink != null)
            {
                var httpSink = request.HttpSink;

                if (string.IsNullOrWhiteSpace(httpSink.Url) || !Uri.TryCreate(httpSink.Url, UriKind.Absolute, out _))
                {
                    return BadRequest(new ApiErrorResponse("httpSink.url must be a valid absolute URI"));
                }

                if (httpSink.MaxConcurrency < 1 || httpSink.MaxConcurrency > 100)
                {
                    return BadRequest(new ApiErrorResponse("httpSink.maxConcurrency must be between 1 and 100"));
                }

                if (httpSink.LockTtlSeconds < 1 || httpSink.LockTtlSeconds > 300)
                {
                    return BadRequest(new ApiErrorResponse("httpSink.lockTtlSeconds must be between 1 and 300"));
                }

                httpSinkConfig = new TopicHttpSinkConfig
                {
                    Url = httpSink.Url,
                    MaxConcurrency = httpSink.MaxConcurrency,
                    LockTtlSeconds = httpSink.LockTtlSeconds
                };
            }

            var result = await _actorInvoker.InvokeMethodAsync<SubscribeRequest, SubscribeResponse>(
                new ActorId(topicId),
                ActorMethodNames.Subscribe,
                new SubscribeRequest { SubscriberId = subscriberId, HttpSink = httpSinkConfig, DedupEnabled = request?.DedupEnabled });

            if (!result.Success)
            {
                if (result.ErrorCode == "SUBSCRIBER_EXISTS")
                {
                    return Conflict(new ApiErrorResponse(result.ErrorMessage ?? "Subscriber already exists"));
                }

                return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to subscribe"));
            }

            return StatusCode(201, new ApiSubscribeResponse(result.Success, result.QueueActorId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error subscribing {subscriberId} to topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Unsubscribe from a topic. Does not delete the subscriber's provisioned QueueActor state.
    /// </summary>
    [HttpDelete("{topicId}/subscribers/{subscriberId}")]
    public async Task<IActionResult> Unsubscribe(string topicId, string subscriberId)
    {
        try
        {
            var result = await _actorInvoker.InvokeMethodAsync<UnsubscribeRequest, UnsubscribeResponse>(
                new ActorId(topicId),
                ActorMethodNames.Unsubscribe,
                new UnsubscribeRequest { SubscriberId = subscriberId });

            if (!result.Success)
            {
                if (result.ErrorCode == "SUBSCRIBER_NOT_FOUND")
                {
                    return NotFound(new ApiErrorResponse(result.ErrorMessage ?? "Subscriber not found"));
                }

                return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to unsubscribe"));
            }

            return Ok(new ApiUnsubscribeResponse(result.Success));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error unsubscribing {subscriberId} from topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// List a topic's current subscribers.
    /// </summary>
    [HttpGet("{topicId}/subscribers")]
    public async Task<IActionResult> ListSubscribers(string topicId)
    {
        try
        {
            var result = await _actorInvoker.InvokeMethodAsync<ListSubscribersResponse>(
                new ActorId(topicId),
                ActorMethodNames.ListSubscribers);

            return Ok(new ApiListSubscribersResponse(result.SubscriberIds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error listing subscribers for topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Read a publish's relay status - an observability aid for in-flight relay, not a
    /// permanent audit log. 404 once the publish's items have been fully delivered and reaped.
    /// </summary>
    [HttpGet("{topicId}/publish/{publishId}")]
    public async Task<IActionResult> GetPublishStatus(string topicId, string publishId)
    {
        try
        {
            var result = await _actorInvoker.InvokeMethodAsync<GetPublishStatusRequest, PublishStatusResponse>(
                new ActorId(topicId),
                ActorMethodNames.GetPublishStatus,
                new GetPublishStatusRequest { PublishId = publishId });

            if (!result.Found)
            {
                return NotFound(new ApiErrorResponse("Publish not found"));
            }

            return Ok(new ApiPublishStatusResponse(result.Complete, result.TargetSubscriberIds, result.DeliveredSubscriberIds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reading publish status for topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Manually reset a subscriber's circuit breaker - the only way out of a blacklist.
    /// </summary>
    [HttpPost("{topicId}/subscribers/{subscriberId}/reset-circuit-breaker")]
    public async Task<IActionResult> ResetCircuitBreaker(string topicId, string subscriberId)
    {
        try
        {
            var result = await _actorInvoker.InvokeMethodAsync<ResetCircuitBreakerRequest, ResetCircuitBreakerResponse>(
                new ActorId(topicId),
                ActorMethodNames.ResetCircuitBreaker,
                new ResetCircuitBreakerRequest { SubscriberId = subscriberId });

            if (!result.Success)
            {
                if (result.ErrorCode == "SUBSCRIBER_NOT_FOUND")
                {
                    return NotFound(new ApiErrorResponse(result.ErrorMessage ?? "Subscriber not found"));
                }

                return BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Failed to reset circuit breaker"));
            }

            return Ok(new ApiResetCircuitBreakerResponse(result.Success));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error resetting circuit breaker for {subscriberId} on topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Read a subscriber's circuit breaker status. 404 when the subscriber is healthy (no
    /// breaker state currently recorded).
    /// </summary>
    [HttpGet("{topicId}/subscribers/{subscriberId}/circuit-breaker")]
    public async Task<IActionResult> GetCircuitBreakerStatus(string topicId, string subscriberId)
    {
        try
        {
            var result = await _actorInvoker.InvokeMethodAsync<GetCircuitBreakerStatusRequest, CircuitBreakerStatusResponse>(
                new ActorId(topicId),
                ActorMethodNames.GetCircuitBreakerStatus,
                new GetCircuitBreakerStatusRequest { SubscriberId = subscriberId });

            if (!result.Found)
            {
                return NotFound(new ApiErrorResponse("No circuit breaker state for this subscriber"));
            }

            return Ok(new ApiCircuitBreakerStatusResponse(result.ConsecutiveFailures, result.FirstFailureAt, result.NextRetryAt, result.Blacklisted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reading circuit breaker status for {subscriberId} on topic {topicId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }
}
