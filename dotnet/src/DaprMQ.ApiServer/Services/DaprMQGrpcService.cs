using Dapr.Actors;
using Grpc.Core;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Grpc;
using ActorModels = DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// gRPC service implementation for DaprMQ operations.
/// Mirrors the HTTP REST API functionality but uses Protocol Buffers and gRPC status codes.
/// </summary>
public class DaprMQGrpcService : Grpc.DaprMQ.DaprMQBase
{
    private readonly ILogger<DaprMQGrpcService> _logger;
    private readonly ActorModels.IQueueActorInvoker _queueActorInvoker;

    public DaprMQGrpcService(
        ILogger<DaprMQGrpcService> logger,
        ActorModels.IQueueActorInvoker queueActorInvoker)
    {
        _logger = logger;
        _queueActorInvoker = queueActorInvoker;
    }

    public override async Task<PushResponse> Push(PushRequest request, ServerCallContext context)
    {
        try
        {
            // Validate items array
            if (request.Items == null || request.Items.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Items array cannot be empty"));
            }

            if (request.Items.Count > 1000)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Maximum 1000 items per push"));
            }

            // Validate priorities
            foreach (var item in request.Items)
            {
                if (item.Priority < 0)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Priority must be non-negative"));
                }
            }

            _logger.LogDebug($"gRPC Push request for queue {request.QueueId} with {request.Items.Count} items");

            var actorId = new ActorId(request.QueueId);

            // Convert gRPC items to actor items
            var actorItems = request.Items.Select(grpcItem => new ActorModels.PushItem
            {
                ItemJson = grpcItem.ItemJson,
                Priority = grpcItem.Priority
            }).ToList();

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.PushRequest, ActorModels.PushResponse>(
                actorId,
                ActorMethodNames.Push,
                new ActorModels.PushRequest
                {
                    Items = actorItems
                },
                context.CancellationToken);

            if (!result.Success)
            {
                throw new RpcException(new Status(StatusCode.Internal, result.ErrorMessage ?? "Failed to push items"));
            }

            return new PushResponse
            {
                Success = result.Success,
                Message = $"Pushed {result.ItemsPushed} items to queue {request.QueueId}",
                ItemsPushed = result.ItemsPushed
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pushing items to queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<PopResponse> Pop(PopRequest request, ServerCallContext context)
    {
        try
        {
            // Count=0 means use default (protobuf default value)
            int count = request.Count > 0 ? request.Count : 1;

            // Validate count is not over max (0 is allowed as default)
            if (request.Count > 100)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Count must be between 1 and 100"));
            }

            _logger.LogDebug($"gRPC Pop request for queue {request.QueueId}, count={count}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.PopRequest, ActorModels.PopResponse>(
                actorId,
                ActorMethodNames.Pop,
                new ActorModels.PopRequest { Count = count },
                context.CancellationToken);

            if (result.IsEmpty)
            {
                return new PopResponse
                {
                    Empty = new PopEmpty { Message = result.Message ?? "Queue is empty" }
                };
            }

            if (result.Locked)
            {
                return new PopResponse
                {
                    Locked = new PopLocked
                    {
                        Message = result.Message ?? "Item is locked",
                        LockExpiresAt = result.LockExpiresAt ?? 0
                    }
                };
            }

            // Return items array
            if (result.Items.Count > 0)
            {
                var popSuccess = new PopSuccess();
                foreach (var item in result.Items)
                {
                    popSuccess.ItemJson.Add(item.ItemJson);
                    popSuccess.Priority.Add(item.Priority);
                }

                return new PopResponse
                {
                    Success = popSuccess
                };
            }

            // Should not reach here but handle gracefully
            return new PopResponse
            {
                Empty = new PopEmpty { Message = "No items available" }
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error popping item from queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<PopWithAckResponse> PopWithAck(PopWithAckRequest request, ServerCallContext context)
    {
        try
        {
            // Count=0 means use default (protobuf default value)
            int count = request.Count > 0 ? request.Count : 1;

            // Validate count is not over max (0 is allowed as default)
            if (request.Count > 100)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Count must be between 1 and 100"));
            }

            _logger.LogDebug($"gRPC PopWithAck request for queue {request.QueueId}, ttl={request.TtlSeconds}s, allow_competing_consumers={request.AllowCompetingConsumers}, count={count}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.PopWithAckRequest, ActorModels.PopWithAckResponse>(
                actorId,
                ActorMethodNames.PopWithAck,
                new ActorModels.PopWithAckRequest
                {
                    TtlSeconds = request.TtlSeconds > 0 ? request.TtlSeconds : 30,
                    Count = count,
                    AllowCompetingConsumers = request.AllowCompetingConsumers
                },
                context.CancellationToken);

            if (result.IsEmpty)
            {
                return new PopWithAckResponse
                {
                    Empty = new PopEmpty { Message = result.Message ?? "Queue is empty" }
                };
            }

            // Check if already locked (Locked=true but no items means it was already locked by another consumer)
            if (result.Locked && result.Items.Count == 0)
            {
                return new PopWithAckResponse
                {
                    Locked = new PopLocked
                    {
                        Message = result.Message ?? "Item is locked",
                        LockExpiresAt = result.LockExpiresAt ?? 0
                    }
                };
            }

            // Success - locks created
            if (result.Items.Count > 0)
            {
                var popSuccess = new PopWithAckSuccess();
                foreach (var item in result.Items)
                {
                    popSuccess.ItemJson.Add(item.ItemJson);
                    popSuccess.Priority.Add(item.Priority);
                    popSuccess.LockId.Add(item.LockId);
                    popSuccess.LockExpiresAt.Add(item.LockExpiresAt);
                }

                return new PopWithAckResponse
                {
                    Success = popSuccess
                };
            }

            // Should not reach here but handle gracefully
            return new PopWithAckResponse
            {
                Empty = new PopEmpty { Message = "No items available" }
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error popping with ack from queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<AcknowledgeResponse> Acknowledge(AcknowledgeRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC Acknowledge request for queue {request.QueueId}, lockId={request.LockId}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                actorId,
                ActorMethodNames.Acknowledge,
                new ActorModels.AcknowledgeRequest
                {
                    LockId = request.LockId
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" => StatusCode.InvalidArgument,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.Message));
            }

            return new AcknowledgeResponse
            {
                Success = result.Success,
                Message = result.Message,
                ItemsAcknowledged = result.ItemsAcknowledged,
                ErrorCode = result.ErrorCode ?? ""
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error acknowledging item in queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<ExtendLockResponse> ExtendLock(ExtendLockRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC ExtendLock request for queue {request.QueueId}, lockId={request.LockId}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.ExtendLockRequest, ActorModels.ExtendLockResponse>(
                actorId,
                ActorMethodNames.ExtendLock,
                new ActorModels.ExtendLockRequest
                {
                    LockId = request.LockId,
                    AdditionalTtlSeconds = request.AdditionalTtlSeconds > 0 ? request.AdditionalTtlSeconds : 30
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" or "INVALID_TTL" => StatusCode.InvalidArgument,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.ErrorMessage ?? "Lock extension failed"));
            }

            return new ExtendLockResponse
            {
                Success = result.Success,
                NewExpiresAt = result.NewExpiresAt,
                ErrorCode = result.ErrorCode ?? "",
                ErrorMessage = result.ErrorMessage ?? ""
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extending lock in queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<DeadLetterResponse> DeadLetter(DeadLetterRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC DeadLetter request for queue {request.QueueId}, lockId={request.LockId}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                actorId,
                ActorMethodNames.DeadLetter,
                new ActorModels.DeadLetterRequest
                {
                    LockId = request.LockId
                },
                context.CancellationToken);

            if (result.Status == "ERROR")
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" => StatusCode.InvalidArgument,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.Message ?? "Failed to move item to dead letter queue"));
            }

            return new DeadLetterResponse
            {
                Success = new DeadLetterSuccess
                {
                    DlqId = result.DlqId ?? ""
                }
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error moving item to dead letter queue in {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }
}
