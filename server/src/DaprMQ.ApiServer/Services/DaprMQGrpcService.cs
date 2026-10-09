using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly ActorModels.ISessionCoordinatorActorInvoker _sessionCoordinatorActorInvoker;

    /// <summary>
    /// ConsumeSession's fallback wait between polls, when the window is full or the queue is empty.
    /// Overridable so tests can tell an ack-driven refill apart from a timer tick.
    /// </summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Cap on a Consume stream's wait while its queue stays empty: the wait doubles from
    /// PollInterval up to this, and resets on a delivery. Each idle stream polls its queue actor,
    /// so without this, N idle consumers cost about 5N actor calls a second.
    /// </summary>
    internal TimeSpan MaxIdlePollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on applying one Ack/DeadLetter/Nack frame on a ConsumeSession stream. Settlement runs
    /// on its own token, not the call's: the client was already told the ack succeeded, so the
    /// client going away mid-ack must not abandon it.
    /// </summary>
    internal TimeSpan SettleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public DaprMQGrpcService(
        ILogger<DaprMQGrpcService> logger,
        ActorModels.IQueueActorInvoker queueActorInvoker,
        ActorModels.ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker)
    {
        _logger = logger;
        _queueActorInvoker = queueActorInvoker;
        _sessionCoordinatorActorInvoker = sessionCoordinatorActorInvoker;
    }

    /// <summary>
    /// Resolves the ActorId an Enqueue (or a session lease op) should target - the plain queue actor
    /// when sessionId is absent, or its dedicated per-session QueueActor instance otherwise.
    /// Mirrors QueueController.ResolveTargetActorId (plan §2.2 - routing is a
    /// controller/gRPC-service-layer concern, never an actor-to-actor forward).
    /// </summary>
    private static string ResolveTargetActorId(string queueId, string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? queueId : $"{queueId}-session-{sessionId}";

    public override async Task<EnqueueResponse> Enqueue(EnqueueRequest request, ServerCallContext context)
    {
        try
        {
            // Validate items array
            if (request.Items == null || request.Items.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Items array cannot be empty"));
            }

            if (request.Items.Count > 10000)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Maximum 10000 items per enqueue"));
            }

            // Validate priorities
            foreach (var item in request.Items)
            {
                if (item.Priority < 0)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Priority must be non-negative"));
                }
            }

            _logger.LogDebug($"gRPC Enqueue request for queue {request.QueueId} with {request.Items.Count} items");

            // Group by target actor (the plain queue, or a per-session QueueActor instance) so
            // each actor call stays a single batched Enqueue - a mixed-session batch costs one Enqueue
            // per distinct session, an ordinary (session-free) batch costs exactly what it always
            // did: one call (plan §2.2, invariant 2). Mirrors QueueController.Enqueue.
            var groups = request.Items.GroupBy(grpcItem => ResolveTargetActorId(request.QueueId, grpcItem.HasSessionId ? grpcItem.SessionId : null));

            int totalEnqueued = 0;
            int totalDeduplicated = 0;
            foreach (var group in groups)
            {
                var actorItems = group.Select(grpcItem => new ActorModels.EnqueueItem
                {
                    ItemJson = grpcItem.ItemJson,
                    Priority = grpcItem.Priority,
                    IdempotencyKey = grpcItem.HasIdempotencyKey ? grpcItem.IdempotencyKey : null
                }).ToList();

                var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                    new ActorId(group.Key),
                    ActorMethodNames.Enqueue,
                    new ActorModels.EnqueueRequest { Items = actorItems },
                    context.CancellationToken);

                if (!result.Success)
                {
                    throw new RpcException(new Status(StatusCode.Internal, result.ErrorMessage ?? "Failed to enqueue items"));
                }

                totalEnqueued += result.ItemsEnqueued;
                totalDeduplicated += result.ItemsDeduplicated;
            }

            return new EnqueueResponse
            {
                Success = true,
                Message = $"Enqueued {totalEnqueued} items to queue {request.QueueId}",
                ItemsEnqueued = totalEnqueued,
                ItemsDeduplicated = totalDeduplicated
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error enqueuing items to queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error enqueuing items to queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<DequeueResponse> Dequeue(DequeueRequest request, ServerCallContext context)
    {
        try
        {
            // Count=0 means use default (protobuf default value)
            int count = request.Count > 0 ? request.Count : 1;

            // Validate count is not over max (0 is allowed as default)
            if (request.Count > 1000)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Count must be between 1 and 1000"));
            }

            _logger.LogDebug($"gRPC Dequeue request for queue {request.QueueId}, count={count}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                actorId,
                ActorMethodNames.Dequeue,
                new ActorModels.DequeueRequest { Count = count, LeaseId = request.HasLeaseId ? request.LeaseId : null },
                context.CancellationToken);

            if (result.ErrorCode != null)
            {
                var leaseStatusCode = result.ErrorCode switch
                {
                    "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    _ => StatusCode.InvalidArgument
                };

                throw new RpcException(new Status(leaseStatusCode, result.Message ?? "Invalid session lease"));
            }

            if (result.IsEmpty)
            {
                return new DequeueResponse
                {
                    Empty = new DequeueEmpty { Message = result.Message ?? "Queue is empty" }
                };
            }

            if (result.Locked)
            {
                return new DequeueResponse
                {
                    Locked = new DequeueBlocked
                    {
                        Message = result.Message ?? "Item is locked",
                        LockExpiresAt = result.LockExpiresAt ?? 0
                    }
                };
            }

            // Return items array
            if (result.Items.Count > 0)
            {
                var dequeueSuccess = new DequeueSuccess();
                foreach (var item in result.Items)
                {
                    dequeueSuccess.ItemJson.Add(item.ItemJson);
                    dequeueSuccess.Priority.Add(item.Priority);
                }

                return new DequeueResponse
                {
                    Success = dequeueSuccess
                };
            }

            // Should not reach here but handle gracefully
            return new DequeueResponse
            {
                Empty = new DequeueEmpty { Message = "No items available" }
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error dequeuing item from queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error dequeuing item from queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<DequeueLockedResponse> DequeueLocked(DequeueLockedRequest request, ServerCallContext context)
    {
        try
        {
            // Count=0 means use default (protobuf default value)
            int count = request.Count > 0 ? request.Count : 1;

            // Validate count is not over max (0 is allowed as default)
            if (request.Count > 1000)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Count must be between 1 and 1000"));
            }

            _logger.LogDebug($"gRPC DequeueLocked request for queue {request.QueueId}, ttl={request.TtlSeconds}s, allow_competing_consumers={request.AllowCompetingConsumers}, count={count}");

            var actorId = new ActorId(request.QueueId);

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                actorId,
                ActorMethodNames.DequeueLocked,
                new ActorModels.DequeueLockedRequest
                {
                    TtlSeconds = request.TtlSeconds > 0 ? request.TtlSeconds : 30,
                    Count = count,
                    AllowCompetingConsumers = request.AllowCompetingConsumers,
                    LeaseId = request.HasLeaseId ? request.LeaseId : null
                },
                context.CancellationToken);

            if (result.ErrorCode != null)
            {
                var leaseStatusCode = result.ErrorCode switch
                {
                    "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    _ => StatusCode.InvalidArgument
                };

                throw new RpcException(new Status(leaseStatusCode, result.Message ?? "Invalid session lease"));
            }

            if (result.IsEmpty)
            {
                return new DequeueLockedResponse
                {
                    Empty = new DequeueEmpty { Message = result.Message ?? "Queue is empty" }
                };
            }

            // Check if already locked (Locked=true but no items means it was already locked by another consumer)
            if (result.Locked && result.Items.Count == 0)
            {
                return new DequeueLockedResponse
                {
                    Locked = new DequeueBlocked
                    {
                        Message = result.Message ?? "Item is locked",
                        LockExpiresAt = result.LockExpiresAt ?? 0
                    }
                };
            }

            // Success - locks created
            if (result.Items.Count > 0)
            {
                var dequeueSuccess = new DequeueLockedSuccess();
                foreach (var item in result.Items)
                {
                    dequeueSuccess.ItemJson.Add(item.ItemJson);
                    dequeueSuccess.Priority.Add(item.Priority);
                    dequeueSuccess.LockId.Add(item.LockId);
                    dequeueSuccess.LockExpiresAt.Add(item.LockExpiresAt);
                }

                return new DequeueLockedResponse
                {
                    Success = dequeueSuccess
                };
            }

            // Should not reach here but handle gracefully
            return new DequeueLockedResponse
            {
                Empty = new DequeueEmpty { Message = "No items available" }
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error dequeuing with ack from queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error dequeuing with ack from queue {request.QueueId}");
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
                    LockId = request.LockId,
                    LeaseId = request.HasLeaseId ? request.LeaseId : null
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" or "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" or "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
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
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error acknowledging item in queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error acknowledging item in queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<AcknowledgeBatchResponse> AcknowledgeBatch(AcknowledgeBatchRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC AcknowledgeBatch request for queue {request.QueueId}, {request.LockIds.Count} lock ids");

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeBatchRequest, ActorModels.AcknowledgeBatchResponse>(
                new ActorId(request.QueueId),
                ActorMethodNames.AcknowledgeBatch,
                new ActorModels.AcknowledgeBatchRequest
                {
                    LockIds = request.LockIds.ToList(),
                    LeaseId = request.HasLeaseId ? request.LeaseId : null
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    "VALIDATION_ERROR" or "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.Message));
            }

            var response = new AcknowledgeBatchResponse
            {
                Success = result.Success,
                Message = result.Message,
                ItemsAcknowledged = result.ItemsAcknowledged,
                ErrorCode = result.ErrorCode ?? ""
            };
            response.Results.Add(result.Results.Select(r => new AcknowledgeResult { LockId = r.LockId, Outcome = r.Outcome }));
            return response;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error acknowledging batch in queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error acknowledging batch in queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<NackResponse> Nack(NackRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC Nack request for queue {request.QueueId}, lockId={request.LockId}");

            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                new ActorId(request.QueueId),
                ActorMethodNames.Nack,
                new ActorModels.NackRequest
                {
                    LockId = request.LockId,
                    LeaseId = request.HasLeaseId ? request.LeaseId : null
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" or "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" or "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.Message));
            }

            return new NackResponse
            {
                Success = result.Success,
                Message = result.Message,
                ErrorCode = result.ErrorCode ?? "",
                DeadLettered = result.DeadLettered,
                DeliveryCount = result.DeliveryCount,
                DlqId = result.DlqId ?? ""
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error nacking item in queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error nacking item in queue {request.QueueId}");
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
                    AdditionalTtlSeconds = request.AdditionalTtlSeconds > 0 ? request.AdditionalTtlSeconds : 30,
                    LeaseId = request.HasLeaseId ? request.LeaseId : null
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" or "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" or "INVALID_TTL" or "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
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
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error extending lock in queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
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
                    LockId = request.LockId,
                    LeaseId = request.HasLeaseId ? request.LeaseId : null
                },
                context.CancellationToken);

            if (result.Status == "ERROR")
            {
                var statusCode = result.ErrorCode switch
                {
                    "LOCK_EXPIRED" or "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    "LOCK_NOT_FOUND" => StatusCode.NotFound,
                    "INVALID_LOCK_ID" or "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
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
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error moving item to dead letter queue in {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error moving item to dead letter queue in {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<AcceptSessionResponse> AcceptSession(AcceptSessionRequest request, ServerCallContext context)
    {
        try
        {
            var sessionId = request.HasSessionId ? request.SessionId : null;
            _logger.LogDebug($"gRPC AcceptSession request for queue {request.QueueId}, sessionId={sessionId ?? "<any>"}");

            var leaseSeconds = request.LeaseSeconds > 0 ? request.LeaseSeconds : 30;

            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                new ActorId(request.QueueId),
                ActorMethodNames.AcceptSession,
                new ActorModels.AcceptSessionRequest { SessionId = sessionId, LeaseSeconds = leaseSeconds },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "SESSION_NOT_FOUND" or "NO_SESSIONS_AVAILABLE" => StatusCode.NotFound,
                    "SESSION_LOCKED" => StatusCode.FailedPrecondition,
                    "SESSION_ACTOR_UNAVAILABLE" => StatusCode.Unavailable,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.ErrorMessage ?? "Failed to accept session"));
            }

            return new AcceptSessionResponse
            {
                SessionId = result.SessionId!,
                LeaseId = result.LeaseId!,
                LeaseExpiresAt = result.LeaseExpiresAt!.Value
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error accepting session for queue {request.QueueId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error accepting session for queue {request.QueueId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<RenewSessionLeaseResponse> RenewSessionLease(RenewSessionLeaseRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC RenewSessionLease request for queue {request.QueueId}, sessionId={request.SessionId}");

            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.RenewSessionLeaseRequest, ActorModels.RenewSessionLeaseResponse>(
                new ActorId(request.QueueId),
                ActorMethodNames.RenewSessionLease,
                new ActorModels.RenewSessionLeaseRequest
                {
                    SessionId = request.SessionId,
                    LeaseId = request.LeaseId,
                    AdditionalSeconds = request.AdditionalSeconds > 0 ? request.AdditionalSeconds : 30
                },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "SESSION_LEASE_EXPIRED" => StatusCode.FailedPrecondition,
                    "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
                    "SESSION_ACTOR_UNAVAILABLE" => StatusCode.Unavailable,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.ErrorMessage ?? "Failed to renew session lease"));
            }

            return new RenewSessionLeaseResponse
            {
                NewExpiresAt = result.NewExpiresAt
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error renewing session lease for queue {request.QueueId}, session {request.SessionId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error renewing session lease for queue {request.QueueId}, session {request.SessionId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    public override async Task<ReleaseSessionResponse> ReleaseSession(ReleaseSessionRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogDebug($"gRPC ReleaseSession request for queue {request.QueueId}, sessionId={request.SessionId}");

            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                new ActorId(request.QueueId),
                ActorMethodNames.ReleaseSession,
                new ActorModels.ReleaseSessionRequest { SessionId = request.SessionId, LeaseId = request.LeaseId },
                context.CancellationToken);

            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "INVALID_LEASE_ID" => StatusCode.InvalidArgument,
                    _ => StatusCode.Internal
                };

                throw new RpcException(new Status(statusCode, result.ErrorMessage ?? "Failed to release session"));
            }

            return new ReleaseSessionResponse { Success = true };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (ActorModels.ActorCallException ex)
        {
            _logger.LogWarning(ex, $"Error releasing session for queue {request.QueueId}, session {request.SessionId}");
            throw DeliveryFailures.ToRpcException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error releasing session for queue {request.QueueId}, session {request.SessionId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Managed consume loop for exactly one session: claims it (any-available or targeted),
    /// streams delivered items back, accepts Ack/DeadLetter/Nack frames, and renews the lease on its
    /// own schedule for as long as the stream stays open - no client heartbeat needed. On
    /// disconnect (or a fatal lease failure) the session is released immediately. One stream =
    /// one session - see plan §2.4.1 for why multiplexing many sessions over one stream was
    /// rejected in favor of one stream per session over a shared gRPC channel.
    /// </summary>
    public override async Task ConsumeSession(
        IAsyncStreamReader<ConsumeSessionRequest> requestStream,
        IServerStreamWriter<ConsumeSessionResponse> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) ||
            requestStream.Current.PayloadCase != ConsumeSessionRequest.PayloadOneofCase.Start)
        {
            await responseStream.WriteAsync(new ConsumeSessionResponse
            {
                Error = new SessionError { ErrorCode = "INVALID_ARGUMENT", Message = "First message on a ConsumeSession stream must be Start" }
            });
            return;
        }

        var start = requestStream.Current.Start;
        var requestedSessionId = start.HasSessionId ? start.SessionId : null;
        var leaseSeconds = start.LeaseSeconds > 0 ? start.LeaseSeconds : 30;
        var prefetchCount = start.PrefetchCount > 0 ? start.PrefetchCount : 1;
        var sessionIdleTimeoutSeconds = start.SessionIdleTimeoutSeconds > 0 ? start.SessionIdleTimeoutSeconds : leaseSeconds;

        _logger.LogDebug($"gRPC ConsumeSession request for queue {start.QueueId}, sessionId={requestedSessionId ?? "<any>"}");

        var acceptResult = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
            new ActorId(start.QueueId),
            ActorMethodNames.AcceptSession,
            new ActorModels.AcceptSessionRequest { SessionId = requestedSessionId, LeaseSeconds = leaseSeconds },
            context.CancellationToken);

        if (!acceptResult.Success)
        {
            await responseStream.WriteAsync(new ConsumeSessionResponse
            {
                Error = new SessionError { ErrorCode = acceptResult.ErrorCode ?? "UNKNOWN", Message = acceptResult.ErrorMessage ?? "Failed to accept session" }
            });
            return;
        }

        var sessionId = acceptResult.SessionId!;
        var leaseId = acceptResult.LeaseId!;
        var sessionActorId = new ActorId(ResolveTargetActorId(start.QueueId, sessionId));

        await responseStream.WriteAsync(new ConsumeSessionResponse
        {
            SessionAssigned = new SessionAssigned { SessionId = sessionId, LeaseExpiresAt = acceptResult.LeaseExpiresAt!.Value }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        int outstanding = 0;

        // Released when acks drain the window to half full, so the poll loop refills while the
        // client still has messages in hand instead of waiting out PollInterval.
        using var refillSignal = new SemaphoreSlim(0, 1);
        var refillThreshold = prefetchCount / 2;
        void Settled()
        {
            if (Interlocked.Decrement(ref outstanding) == refillThreshold && refillSignal.CurrentCount == 0)
            {
                try
                {
                    refillSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already signalled - a rare double release is harmless.
                }
            }
        }

        // Reads Ack/DeadLetter/Nack frames from the client for the life of the stream. Cancels the
        // shared token when the client closes its send side (natural end, no exception) or when
        // the poll loop below cancels first (a fatal lease failure) - either way, the other side
        // unwinds promptly.
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (await requestStream.MoveNext(cts.Token))
                {
                    var req = requestStream.Current;
                    if (req.PayloadCase == ConsumeSessionRequest.PayloadOneofCase.Ack)
                    {
                        using var settleCts = new CancellationTokenSource(SettleTimeout);
                        var ackResult = await _queueActorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                            sessionActorId,
                            ActorMethodNames.Acknowledge,
                            new ActorModels.AcknowledgeRequest { LockId = req.Ack.LockId, LeaseId = leaseId },
                            settleCts.Token);

                        // Surface a rejected settlement rather than letting it look like success. The
                        // item is still outstanding server-side, so a silent failure here would leave
                        // the client believing it had completed a message it had not.
                        if (!ackResult.Success)
                        {
                            _logger.LogWarning(
                                "Acknowledge rejected for lock {LockId} on session {SessionId}: {ErrorCode} {Message}",
                                req.Ack.LockId, sessionId, ackResult.ErrorCode, ackResult.Message);

                            await responseStream.WriteAsync(new ConsumeSessionResponse
                            {
                                Error = new SessionError
                                {
                                    ErrorCode = ackResult.ErrorCode ?? "ACK_FAILED",
                                    Message = ackResult.Message ?? "Failed to acknowledge message"
                                }
                            });
                        }

                        Settled();
                    }
                    else if (req.PayloadCase == ConsumeSessionRequest.PayloadOneofCase.DeadLetter)
                    {
                        using var settleCts = new CancellationTokenSource(SettleTimeout);
                        var dlqResult = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                            sessionActorId,
                            ActorMethodNames.DeadLetter,
                            new ActorModels.DeadLetterRequest { LockId = req.DeadLetter.LockId, LeaseId = leaseId },
                            settleCts.Token);

                        if (dlqResult.Status != "SUCCESS")
                        {
                            _logger.LogWarning(
                                "DeadLetter rejected for lock {LockId} on session {SessionId}: {ErrorCode} {Message}",
                                req.DeadLetter.LockId, sessionId, dlqResult.ErrorCode, dlqResult.Message);

                            await responseStream.WriteAsync(new ConsumeSessionResponse
                            {
                                Error = new SessionError
                                {
                                    ErrorCode = dlqResult.ErrorCode ?? "DEAD_LETTER_FAILED",
                                    Message = dlqResult.Message ?? "Failed to dead-letter message"
                                }
                            });
                        }

                        Settled();
                    }
                    else if (req.PayloadCase == ConsumeSessionRequest.PayloadOneofCase.Nack)
                    {
                        // The item goes back to the head of the session, so the poll loop below
                        // redelivers it without anything further here.
                        using var settleCts = new CancellationTokenSource(SettleTimeout);
                        var nackResult = await _queueActorInvoker.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                            sessionActorId,
                            ActorMethodNames.Nack,
                            new ActorModels.NackRequest { LockId = req.Nack.LockId, LeaseId = leaseId },
                            settleCts.Token);

                        if (!nackResult.Success)
                        {
                            _logger.LogWarning(
                                "Nack rejected for lock {LockId} on session {SessionId}: {ErrorCode} {Message}",
                                req.Nack.LockId, sessionId, nackResult.ErrorCode, nackResult.Message);

                            await responseStream.WriteAsync(new ConsumeSessionResponse
                            {
                                Error = new SessionError
                                {
                                    ErrorCode = nackResult.ErrorCode ?? "NACK_FAILED",
                                    Message = nackResult.Message ?? "Failed to nack message"
                                }
                            });
                        }

                        Settled();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation from either side.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading ConsumeSession request stream for session {SessionId}", sessionId);
            }
            finally
            {
                cts.Cancel();
            }
        });

        double lastRenewalAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var renewIntervalSeconds = Math.Max(1, leaseSeconds / 2.0);
        double? emptySince = null;

        try
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastRenewalAt >= renewIntervalSeconds)
                {
                    var renewResult = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.RenewSessionLeaseRequest, ActorModels.RenewSessionLeaseResponse>(
                        new ActorId(start.QueueId),
                        ActorMethodNames.RenewSessionLease,
                        new ActorModels.RenewSessionLeaseRequest { SessionId = sessionId, LeaseId = leaseId, AdditionalSeconds = leaseSeconds },
                        cts.Token);

                    if (!renewResult.Success)
                    {
                        await responseStream.WriteAsync(new ConsumeSessionResponse
                        {
                            SessionLost = new SessionLost { Message = renewResult.ErrorMessage ?? "Failed to renew session lease" }
                        });
                        break;
                    }

                    lastRenewalAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                var capacity = prefetchCount - Volatile.Read(ref outstanding);
                var windowFull = capacity <= 0;
                if (!windowFull)
                {
                    var dequeueResult = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                        sessionActorId,
                        ActorMethodNames.DequeueLocked,
                        new ActorModels.DequeueLockedRequest
                        {
                            Count = capacity,
                            TtlSeconds = leaseSeconds,
                            LeaseId = leaseId,
                            AllowCompetingConsumers = true
                        },
                        cts.Token);

                    if (dequeueResult.ErrorCode != null)
                    {
                        await responseStream.WriteAsync(new ConsumeSessionResponse
                        {
                            SessionLost = new SessionLost { Message = dequeueResult.Message ?? "Session lease lost" }
                        });
                        break;
                    }

                    if (dequeueResult.Items.Count > 0)
                    {
                        emptySince = null;
                        foreach (var item in dequeueResult.Items)
                        {
                            Interlocked.Increment(ref outstanding);
                            await responseStream.WriteAsync(new ConsumeSessionResponse
                            {
                                Delivered = new SessionDelivered
                                {
                                    LockId = item.LockId,
                                    ItemJson = item.ItemJson,
                                    Priority = item.Priority,
                                    LockExpiresAt = item.LockExpiresAt
                                }
                            });
                        }

                        continue;
                    }
                }

                // Idle-drain: no message and nothing in flight for sessionIdleTimeoutSeconds -
                // release the session and end the stream (not an error) rather than holding it,
                // and its lease, indefinitely. Mirrors Azure Service Bus's
                // ServiceBusSessionProcessorOptions.SessionIdleTimeout.
                if (Volatile.Read(ref outstanding) == 0)
                {
                    double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    emptySince ??= now;
                    if (now - emptySince >= sessionIdleTimeoutSeconds)
                    {
                        await responseStream.WriteAsync(new ConsumeSessionResponse
                        {
                            SessionDrained = new SessionDrained { SessionId = sessionId }
                        });
                        break;
                    }
                }
                else
                {
                    emptySince = null;
                }

                // Window full: wake early once acks half-drain it. Queue empty: an ack isn't a
                // reason to look for new messages, so wait out the poll interval.
                if (windowFull)
                {
                    await refillSignal.WaitAsync(PollInterval, cts.Token);
                }
                else
                {
                    await Task.Delay(PollInterval, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown - the client closed the stream, or we cancelled ourselves above
            // after writing SessionLost.
        }
        finally
        {
            cts.Cancel();
            try
            {
                await readerTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error awaiting ConsumeSession reader task for session {SessionId}", sessionId);
            }

            try
            {
                await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                    new ActorId(start.QueueId),
                    ActorMethodNames.ReleaseSession,
                    new ActorModels.ReleaseSessionRequest { SessionId = sessionId, LeaseId = leaseId },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release session {SessionId} after ConsumeSession stream ended", sessionId);
            }
        }
    }

    /// <summary>
    /// Managed consume loop for a plain (non-session) queue: keeps up to prefetch_count locked items
    /// delivered, refills as Ack/Nack/DeadLetter frames settle them, and renews the locks of every
    /// delivered-but-unsettled item for as long as the stream is open, so the client never calls
    /// ExtendLock. When the stream ends, every item still outstanding is nacked straight back to its
    /// position rather than waiting out its lock.
    /// </summary>
    public override async Task Consume(
        IAsyncStreamReader<ConsumeRequest> requestStream,
        IServerStreamWriter<ConsumeResponse> responseStream,
        ServerCallContext context)
    {
        // The reader and the poll loop both write, and gRPC forbids overlapping writes on one stream.
        using var writeLock = new SemaphoreSlim(1, 1);
        async Task WriteAsync(ConsumeResponse response)
        {
            await writeLock.WaitAsync();
            try
            {
                await responseStream.WriteAsync(response);
            }
            finally
            {
                writeLock.Release();
            }
        }

        static ConsumeResponse Error(string code, string message) =>
            new() { Error = new ConsumeError { ErrorCode = code, Message = message } };

        static ConsumeResponse SettleFailed(string lockId, string code, string? message) =>
            new() { SettleFailed = new ConsumeSettleFailed { LockId = lockId, ErrorCode = code, Message = message ?? string.Empty } };

        if (!await requestStream.MoveNext(context.CancellationToken) ||
            requestStream.Current.PayloadCase != ConsumeRequest.PayloadOneofCase.Start)
        {
            await WriteAsync(Error("INVALID_ARGUMENT", "First message on a Consume stream must be Start"));
            return;
        }

        var start = requestStream.Current.Start;
        if (string.IsNullOrWhiteSpace(start.QueueId))
        {
            await WriteAsync(Error("INVALID_ARGUMENT", "queue_id is required"));
            return;
        }

        var prefetchCount = Math.Clamp(start.PrefetchCount > 0 ? start.PrefetchCount : 1, 1, 1000);
        var lockTtlSeconds = Math.Clamp(start.LockTtlSeconds > 0 ? start.LockTtlSeconds : 30, 1, 300);
        // Renewal resets each lock to lockTtlSeconds from now, so a tick that runs late loses nothing.
        // Every third of the TTL leaves room for a renewal call that's slow or a tick that's late.
        var renewInterval = TimeSpan.FromSeconds(lockTtlSeconds / 3.0);
        var actorId = new ActorId(start.QueueId);

        _logger.LogDebug("gRPC Consume request for queue {QueueId}, prefetch={Prefetch}", start.QueueId, prefetchCount);

        // Locks delivered and not yet settled: renewed while the stream is open, nacked when it ends.
        var outstanding = new ConcurrentDictionary<string, byte>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        // Released when settles drain the window to half full, so the poll loop refills while the
        // client still has messages in hand (ADR 0001).
        using var refillSignal = new SemaphoreSlim(0, 1);
        var refillThreshold = prefetchCount / 2;
        void SignalRefill()
        {
            if (outstanding.Count <= refillThreshold && refillSignal.CurrentCount == 0)
            {
                try
                {
                    refillSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already signalled - a rare double release is harmless.
                }
            }
        }

        // Settle frames the reader has accepted, applied by the settler below. Acks that pile up
        // while one batch is in flight go to the actor together in the next AcknowledgeBatch, so a
        // stream isn't capped at one Acknowledge round trip per message.
        var settles = Channel.CreateUnbounded<ConsumeRequest>();

        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (await requestStream.MoveNext(cts.Token))
                {
                    var req = requestStream.Current;
                    var lockId = req.PayloadCase switch
                    {
                        ConsumeRequest.PayloadOneofCase.Ack => req.Ack.LockId,
                        ConsumeRequest.PayloadOneofCase.Nack => req.Nack.LockId,
                        ConsumeRequest.PayloadOneofCase.DeadLetter => req.DeadLetter.LockId,
                        _ => null
                    };
                    if (lockId == null)
                    {
                        continue;
                    }

                    // Stop renewing before settling, so a renewal can't race the settle. A lock that
                    // isn't outstanding was never delivered here, or its renewal already found it lost.
                    if (!outstanding.TryRemove(lockId, out _))
                    {
                        await WriteAsync(SettleFailed(lockId, "LOCK_NOT_FOUND", "Lock is not outstanding on this stream"));
                        continue;
                    }

                    settles.Writer.TryWrite(req);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation from either side.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading Consume request stream for queue {QueueId}", start.QueueId);
            }
            finally
            {
                settles.Writer.TryComplete();
                cts.Cancel();
            }
        });

        // Runs until the reader completes the channel, not until the call is cancelled: every frame
        // the reader accepted is applied even if the client has gone away.
        var settlerTask = Task.Run(async () =>
        {
            while (await settles.Reader.WaitToReadAsync())
            {
                var batch = new List<ConsumeRequest>();
                while (batch.Count < MaxSettleBatchSize && settles.Reader.TryRead(out var req))
                {
                    batch.Add(req);
                }

                var acks = batch.Where(r => r.PayloadCase == ConsumeRequest.PayloadOneofCase.Ack).Select(r => r.Ack.LockId).ToList();
                if (acks.Count > 0)
                {
                    foreach (var (lockId, errorCode, message) in await AcknowledgeAsync(actorId, acks))
                    {
                        _logger.LogWarning("Ack rejected for lock {LockId} on queue {QueueId}: {ErrorCode} {Message}", lockId, start.QueueId, errorCode, message);
                        await WriteAsync(SettleFailed(lockId, errorCode, message));
                    }
                }

                foreach (var req in batch.Where(r => r.PayloadCase != ConsumeRequest.PayloadOneofCase.Ack))
                {
                    var (errorCode, message) = await SettleAsync(actorId, req);
                    if (errorCode != null)
                    {
                        var lockId = req.PayloadCase == ConsumeRequest.PayloadOneofCase.Nack ? req.Nack.LockId : req.DeadLetter.LockId;
                        _logger.LogWarning("{Operation} rejected for lock {LockId} on queue {QueueId}: {ErrorCode} {Message}",
                            req.PayloadCase, lockId, start.QueueId, errorCode, message);
                        await WriteAsync(SettleFailed(lockId, errorCode, message));
                    }
                }

                SignalRefill();
            }
        });

        var lastRenewal = DateTime.UtcNow;
        var idleWait = PollInterval;
        try
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                if (DateTime.UtcNow - lastRenewal >= renewInterval)
                {
                    await RenewLocksAsync(actorId, outstanding, lockTtlSeconds, cts.Token);
                    lastRenewal = DateTime.UtcNow;
                }

                var capacity = prefetchCount - outstanding.Count;
                if (capacity <= 0)
                {
                    await refillSignal.WaitAsync(PollInterval, cts.Token);
                    continue;
                }

                ActorModels.DequeueLockedResponse dequeueResult;
                try
                {
                    dequeueResult = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                        actorId,
                        ActorMethodNames.DequeueLocked,
                        new ActorModels.DequeueLockedRequest
                        {
                            Count = capacity,
                            TtlSeconds = lockTtlSeconds,
                            AllowCompetingConsumers = start.AllowCompetingConsumers
                        },
                        cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A worker gap: keep the stream and its locks, and try again shortly.
                    _logger.LogWarning(ex, "Dequeue failed on Consume stream for queue {QueueId}", start.QueueId);
                    await Task.Delay(PollInterval, cts.Token);
                    continue;
                }

                if (dequeueResult.ErrorCode != null)
                {
                    await WriteAsync(Error(dequeueResult.ErrorCode, dequeueResult.Message ?? "Dequeue failed"));
                    break;
                }

                if (dequeueResult.Items.Count == 0)
                {
                    // Empty, or held by another consumer when competing consumers are off. Back off,
                    // but never past the next renewal of locks already delivered.
                    var untilRenewal = renewInterval - (DateTime.UtcNow - lastRenewal);
                    var wait = outstanding.IsEmpty ? idleWait : TimeSpan.FromTicks(Math.Clamp(idleWait.Ticks, 0, Math.Max(untilRenewal.Ticks, 0)));
                    await Task.Delay(wait, cts.Token);
                    idleWait = TimeSpan.FromTicks(Math.Min(idleWait.Ticks * 2, Math.Max(MaxIdlePollInterval.Ticks, PollInterval.Ticks)));
                    continue;
                }

                idleWait = PollInterval;
                foreach (var item in dequeueResult.Items)
                {
                    outstanding[item.LockId] = 0;
                    await WriteAsync(new ConsumeResponse
                    {
                        Delivered = new ConsumeDelivered
                        {
                            LockId = item.LockId,
                            ItemJson = item.ItemJson,
                            Priority = item.Priority,
                            LockExpiresAt = item.LockExpiresAt,
                            DeliveryCount = item.DeliveryCount + 1
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client closed the stream, or the call was cancelled.
        }
        finally
        {
            cts.Cancel();
            try
            {
                await readerTask;
                await settlerTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error awaiting Consume reader or settler for queue {QueueId}", start.QueueId);
            }

            // Return what the client never settled to its position now, not when its lock lapses.
            await Task.WhenAll(outstanding.Keys.Select(async lockId =>
            {
                try
                {
                    using var settleCts = new CancellationTokenSource(SettleTimeout);
                    await _queueActorInvoker.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                        actorId, ActorMethodNames.Nack, new ActorModels.NackRequest { LockId = lockId }, settleCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to nack lock {LockId} on queue {QueueId} after Consume stream ended", lockId, start.QueueId);
                }
            }));
        }
    }

    /// <summary>
    /// Acknowledges the locks in one AcknowledgeBatch call, on its own token like SettleAsync.
    /// Returns the locks that weren't acknowledged, with why.
    /// </summary>
    private async Task<List<(string LockId, string ErrorCode, string? Message)>> AcknowledgeAsync(ActorId actorId, List<string> lockIds)
    {
        using var settleCts = new CancellationTokenSource(SettleTimeout);
        try
        {
            var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeBatchRequest, ActorModels.AcknowledgeBatchResponse>(
                actorId, ActorMethodNames.AcknowledgeBatch, new ActorModels.AcknowledgeBatchRequest { LockIds = lockIds }, settleCts.Token);
            if (!result.Success)
            {
                return lockIds.Select(id => (id, result.ErrorCode ?? "ACK_FAILED", (string?)result.Message)).ToList();
            }
            return result.Results
                .Where(r => r.Outcome != "ACKNOWLEDGED")
                .Select(r => (r.LockId, r.Outcome, (string?)$"Lock was not acknowledged: {r.Outcome}"))
                .ToList();
        }
        catch (Exception ex)
        {
            return lockIds.Select(id => (id, "SETTLE_FAILED", (string?)ex.Message)).ToList();
        }
    }

    /// <summary>
    /// Applies one Nack/DeadLetter frame (acks go through AcknowledgeAsync). Runs on its own token, not the call's: once the frame
    /// is read, the client going away must not abandon the settle. Returns the error code and
    /// message when the actor rejects it, or nulls on success.
    /// </summary>
    private async Task<(string? ErrorCode, string? Message)> SettleAsync(ActorId actorId, ConsumeRequest req)
    {
        using var settleCts = new CancellationTokenSource(SettleTimeout);
        try
        {
            switch (req.PayloadCase)
            {
                case ConsumeRequest.PayloadOneofCase.Ack:
                    var ack = await _queueActorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                        actorId, ActorMethodNames.Acknowledge, new ActorModels.AcknowledgeRequest { LockId = req.Ack.LockId }, settleCts.Token);
                    return ack.Success ? (null, null) : (ack.ErrorCode ?? "ACK_FAILED", ack.Message);

                case ConsumeRequest.PayloadOneofCase.Nack:
                    var nack = await _queueActorInvoker.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                        actorId, ActorMethodNames.Nack, new ActorModels.NackRequest { LockId = req.Nack.LockId }, settleCts.Token);
                    return nack.Success ? (null, null) : (nack.ErrorCode ?? "NACK_FAILED", nack.Message);

                default:
                    var dlq = await _queueActorInvoker.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                        actorId, ActorMethodNames.DeadLetter, new ActorModels.DeadLetterRequest { LockId = req.DeadLetter.LockId }, settleCts.Token);
                    return dlq.Status == "SUCCESS" ? (null, null) : (dlq.ErrorCode ?? "DEAD_LETTER_FAILED", dlq.Message);
            }
        }
        catch (Exception ex)
        {
            return ("SETTLE_FAILED", ex.Message);
        }
    }

    // The actor caps AcknowledgeBatch and ExtendLockBatch at 1000 locks a call.
    private const int MaxSettleBatchSize = 1000;

    /// <summary>
    /// Renews every outstanding lock to lockTtlSeconds from now, in batches of up to 1000. A lock the actor
    /// no longer has is dropped from the set: its item is already on its way back to the queue, and
    /// the client learns that when it tries to settle it. A failed call leaves the set as it is, so
    /// the next tick retries.
    /// </summary>
    private async Task RenewLocksAsync(ActorId actorId, ConcurrentDictionary<string, byte> outstanding, int lockTtlSeconds, CancellationToken cancellationToken)
    {
        foreach (var batch in outstanding.Keys.Chunk(MaxSettleBatchSize))
        {
            try
            {
                var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.ExtendLockBatchRequest, ActorModels.ExtendLockBatchResponse>(
                    actorId,
                    ActorMethodNames.ExtendLockBatch,
                    new ActorModels.ExtendLockBatchRequest { LockIds = batch.ToList(), TtlSeconds = lockTtlSeconds },
                    cancellationToken);

                foreach (var lost in result.Results.Where(r => r.Outcome != "EXTENDED"))
                {
                    outstanding.TryRemove(lost.LockId, out _);
                    _logger.LogWarning("Lock {LockId} lost on Consume stream for queue {QueueId}: {Outcome}", lost.LockId, actorId, lost.Outcome);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Lock renewal failed on Consume stream for queue {QueueId}", actorId);
            }
        }
    }
}
