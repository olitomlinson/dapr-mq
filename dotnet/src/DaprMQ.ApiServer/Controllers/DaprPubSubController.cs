using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using DaprMQ.Interfaces;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.ApiServer.Controllers;

[ApiController]
[Route("queue")]
public class DaprPubSubController : ControllerBase
{
    private readonly ILogger<DaprPubSubController> _logger;
    private readonly IDaprPubSubSinkActorInvoker _daprPubSubSinkActorInvoker;

    public DaprPubSubController(
        ILogger<DaprPubSubController> logger,
        IDaprPubSubSinkActorInvoker daprPubSubSinkActorInvoker)
    {
        _logger = logger;
        _daprPubSubSinkActorInvoker = daprPubSubSinkActorInvoker;
    }

    /// <summary>
    /// Register a Dapr PubSub sink for the specified queue.
    /// </summary>
    [HttpPost("{queueId}/sink/pubsub/register")]
    public async Task<IActionResult> RegisterDaprPubSubSink(
        string queueId,
        [FromBody] ApiRegisterDaprPubSubSinkRequest request)
    {
        try
        {
            // Validate PubSubName
            if (string.IsNullOrWhiteSpace(request.PubSubName))
            {
                return BadRequest(new ApiRegisterDaprPubSubSinkResponse(
                    false,
                    "PubSubName cannot be empty"
                ));
            }

            // Validate Topic
            if (string.IsNullOrWhiteSpace(request.Topic))
            {
                return BadRequest(new ApiRegisterDaprPubSubSinkResponse(
                    false,
                    "Topic cannot be empty"
                ));
            }

            // Validate MaxConcurrency
            if (request.MaxConcurrency < 1 || request.MaxConcurrency > 100)
            {
                return BadRequest(new ApiRegisterDaprPubSubSinkResponse(
                    false,
                    "MaxConcurrency must be between 1 and 100"
                ));
            }

            // Validate LockTtlSeconds
            if (request.LockTtlSeconds < 1 || request.LockTtlSeconds > 300)
            {
                return BadRequest(new ApiRegisterDaprPubSubSinkResponse(
                    false,
                    "LockTtlSeconds must be between 1 and 300"
                ));
            }

            // Calculate sink actor ID
            string sinkActorId = $"{queueId}-pubsub-sink";
            var sinkActorId_ActorId = new ActorId(sinkActorId);

            // Build InitializeDaprPubSubSinkRequest (dynamic polling starts at 1s)
            var initRequest = new InitializeDaprPubSubSinkRequest
            {
                PubSubName = request.PubSubName,
                Topic = request.Topic,
                RawPayload = request.RawPayload,
                QueueActorId = queueId,
                MaxConcurrency = request.MaxConcurrency,
                LockTtlSeconds = request.LockTtlSeconds
            };

            // Initialize DaprPubSubSinkActor (registers reminder, starts polling)
            await _daprPubSubSinkActorInvoker.InvokeMethodAsync(
                sinkActorId_ActorId,
                ActorMethodNames.InitializeDaprPubSubSink,
                initRequest);

            return Ok(new ApiRegisterDaprPubSubSinkResponse(
                true,
                "DaprPubSub sink registered successfully",
                DaprPubSubSinkActorId: sinkActorId
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error registering DaprPubSub sink for {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Unregister the Dapr PubSub sink for the specified queue.
    /// </summary>
    [HttpPost("{queueId}/sink/pubsub/unregister")]
    public async Task<IActionResult> UnregisterDaprPubSubSink(string queueId)
    {
        try
        {
            // Calculate sink actor ID
            string sinkActorId = $"{queueId}-pubsub-sink";
            var sinkActorId_ActorId = new ActorId(sinkActorId);

            // Uninitialize DaprPubSubSinkActor (unregisters reminder)
            await _daprPubSubSinkActorInvoker.InvokeMethodAsync(
                sinkActorId_ActorId,
                ActorMethodNames.UninitializeDaprPubSubSink);

            return Ok(new ApiUnregisterDaprPubSubSinkResponse(
                true,
                "DaprPubSub sink unregistered successfully"
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error unregistering DaprPubSub sink for {queueId}");
            return StatusCode(500, new ApiErrorResponse($"Internal error: {ex.Message}"));
        }
    }
}
