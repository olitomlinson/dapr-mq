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
    private readonly ActorModels.IActorInvoker _actorInvoker;

    public DaprMQGrpcService(
        ILogger<DaprMQGrpcService> logger,
        ActorModels.IActorInvoker actorInvoker)
    {
        _logger = logger;
        _actorInvoker = actorInvoker;
    }

    public override async Task<PushResponse> Push(PushRequest request, ServerCallContext context)
    {
        try
        {
            if (request.Priority < 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Priority must be non-negative"));
            }

            _logger.LogDebug($"gRPC Push request for queue {request.QueueId} with priority {request.Priority}");

            var actorId = new ActorId(request.QueueId);

            var result = await _actorInvoker.InvokeMethodAsync<ActorModels.PushRequest, ActorModels.PushResponse>(
                actorId,
                ActorMethodNames.Push,
                new ActorModels.PushRequest
                {
                    ItemJson = request.ItemJson,
                    Priority = request.Priority
                },
                context.CancellationToken);

            return new PushResponse
            {
                Success = result.Success,
                Message = $"Item pushed to queue {request.QueueId} at priority {request.Priority}"
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error pushing item to queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<PopResponse> Pop(PopRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC Pop request for queue {request.QueueId}");

            var actorId = new ActorId(request.QueueId);

            var result = await _actorInvoker.InvokeMethodAsync<ActorModels.PopResponse>(
                actorId,
                ActorMethodNames.Pop,
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

            return new PopResponse
            {
                Success = new PopSuccess
                {
                    ItemJson = result.ItemJson ?? ""
                }
            };
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
            _logger.LogDebug($"gRPC PopWithAck request for queue {request.QueueId}, ttl={request.TtlSeconds}s");

            var actorId = new ActorId(request.QueueId);

            var result = await _actorInvoker.InvokeMethodAsync<ActorModels.PopWithAckRequest, ActorModels.PopWithAckResponse>(
                actorId,
                ActorMethodNames.PopWithAck,
                new ActorModels.PopWithAckRequest
                {
                    TtlSeconds = request.TtlSeconds > 0 ? request.TtlSeconds : 30
                },
                context.CancellationToken);

            if (result.IsEmpty)
            {
                return new PopWithAckResponse
                {
                    Empty = new PopEmpty { Message = result.Message ?? "Queue is empty" }
                };
            }

            // Check if already locked (Locked=true but no LockId means it was already locked by another consumer)
            if (result.Locked && result.LockId == null)
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

            // Success - lock created (Locked=true AND LockId is present)
            return new PopWithAckResponse
            {
                Success = new PopWithAckSuccess
                {
                    ItemJson = result.ItemJson ?? "",
                    LockId = result.LockId ?? "",
                    LockExpiresAt = result.LockExpiresAt ?? 0
                }
            };
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

            var result = await _actorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
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

            var result = await _actorInvoker.InvokeMethodAsync<ActorModels.ExtendLockRequest, ActorModels.ExtendLockResponse>(
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

            var result = await _actorInvoker.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
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
                    DlqActorId = result.DlqActorId ?? ""
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
