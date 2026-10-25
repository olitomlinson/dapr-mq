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
    private readonly SessionReleaseHandoff? _releaseHandoff;

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
        ActorModels.ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker,
        SessionReleaseHandoff? releaseHandoff = null)
    {
        _logger = logger;
        _queueActorInvoker = queueActorInvoker;
        _sessionCoordinatorActorInvoker = sessionCoordinatorActorInvoker;
        _releaseHandoff = releaseHandoff;
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
                    DeliveryFailures.ThrowIfUnavailable(result.ErrorCode, result.ErrorMessage);
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

        // A drained stream's lease parked on this gateway is ended by the same turn as this claim.
        var parked = requestedSessionId == null ? _releaseHandoff?.TryTake(start.QueueId) : null;
        var acceptResult = parked != null
            ? await ReleaseAndAcceptAsync(parked, leaseSeconds, context.CancellationToken)
            : await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
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

        // The reader and the poll loop both write, and gRPC forbids overlapping writes on one stream.
        using var writeLock = new SemaphoreSlim(1, 1);
        async Task WriteAsync(ConsumeSessionResponse response)
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

        await WriteAsync(new ConsumeSessionResponse
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

                            await WriteAsync(new ConsumeSessionResponse
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

                            await WriteAsync(new ConsumeSessionResponse
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

                            await WriteAsync(new ConsumeSessionResponse
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
        bool drained = false;

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
                        await WriteAsync(new ConsumeSessionResponse
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
                        await WriteAsync(new ConsumeSessionResponse
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
                            await WriteAsync(new ConsumeSessionResponse
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
                        await WriteAsync(new ConsumeSessionResponse
                        {
                            SessionDrained = new SessionDrained { SessionId = sessionId }
                        });
                        drained = true;
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

            // A drained any-available stream's consumer opens its next stream straight away, so its
            // lease waits briefly for that stream's claim (see SessionReleaseHandoff).
            if (!(drained && requestedSessionId == null && _releaseHandoff?.Park(start.QueueId, sessionId, leaseId) == true))
            {
                await ReleaseQuietlyAsync(start.QueueId, sessionId, leaseId);
            }
        }
    }

    private async Task<ActorModels.AcceptSessionResponse> ReleaseAndAcceptAsync(
        SessionReleaseHandoff.ParkedRelease parked, int leaseSeconds, CancellationToken cancellationToken)
    {
        ActorModels.ReleaseAndAcceptSessionResponse result;
        try
        {
            result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.ReleaseAndAcceptSessionRequest, ActorModels.ReleaseAndAcceptSessionResponse>(
                new ActorId(parked.QueueId),
                ActorMethodNames.ReleaseAndAcceptSession,
                new ActorModels.ReleaseAndAcceptSessionRequest { SessionId = parked.SessionId, LeaseId = parked.LeaseId, LeaseSeconds = leaseSeconds },
                cancellationToken);
        }
        catch
        {
            // The release may not have run; a second one is rejected harmlessly if it did.
            await ReleaseQuietlyAsync(parked.QueueId, parked.SessionId, parked.LeaseId);
            throw;
        }

        if (!result.Release.Success)
        {
            _logger.LogWarning("Failed to release drained session {SessionId}: {ErrorCode}", parked.SessionId, result.Release.ErrorCode);
        }
        return result.Accept;
    }

    private async Task ReleaseQuietlyAsync(string queueId, string sessionId, string leaseId)
    {
        try
        {
            await _sessionCoordinatorActorInvoker.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                new ActorId(queueId),
                ActorMethodNames.ReleaseSession,
                new ActorModels.ReleaseSessionRequest { SessionId = sessionId, LeaseId = leaseId },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release session {SessionId} after ConsumeSession stream ended", sessionId);
        }
    }

    /// <summary>
    /// Managed consume loop for a plain (non-session) queue: keeps up to prefetch_count locked items
    /// delivered, refills as Ack/Nack/DeadLetter frames settle them, and renews the locks of every
    /// delivered-but-unsettled item shortly before each expires (see RenewAt) for as long as the
    /// stream is open, so the client never calls ExtendLock. When the stream ends, every item still outstanding is nacked straight back to its
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
        var renewal = new LockRenewalPolicy(start.LockRenewal == ConsumeLockRenewal.RenewStarted, Math.Max(start.MaxRenewalSeconds, 0));
        var actorId = new ActorId(start.QueueId);

        _logger.LogDebug("gRPC Consume request for queue {QueueId}, prefetch={Prefetch}", start.QueueId, prefetchCount);

        // Locks delivered and not yet settled: renewed while the stream is open (see LockRenewalPolicy),
        // nacked when it ends.
        var outstanding = new ConcurrentDictionary<string, OutstandingLock>();
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
                    if (req.PayloadCase == ConsumeRequest.PayloadOneofCase.Started)
                    {
                        foreach (var startedId in req.Started.LockIds)
                        {
                            if (outstanding.TryGetValue(startedId, out var held))
                            {
                                held.MarkStarted();
                            }
                            else
                            {
                                // It lapsed in the client's buffer, or was never delivered here. As in Service
                                // Bus the handler still runs; settling it will fail.
                                await WriteAsync(SettleFailed(startedId, "LOCK_EXPIRED", "Lock lapsed before its handler started"));
                            }
                        }
                        continue;
                    }

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

        var idleWait = PollInterval;
        try
        {
            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                await RenewDueLocksAsync(actorId, outstanding, renewal, lockTtlSeconds, cts.Token);

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
                    var wait = outstanding.IsEmpty ? idleWait : TimeSpan.FromTicks(Math.Clamp(idleWait.Ticks, 0, Math.Max(UntilNextRenewal(outstanding, renewal).Ticks, 0)));
                    await Task.Delay(wait, cts.Token);
                    idleWait = TimeSpan.FromTicks(Math.Min(idleWait.Ticks * 2, Math.Max(MaxIdlePollInterval.Ticks, PollInterval.Ticks)));
                    continue;
                }

                idleWait = PollInterval;
                foreach (var item in dequeueResult.Items)
                {
                    outstanding[item.LockId] = new OutstandingLock(item.LockExpiresAt);
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
                using var settleCts = new CancellationTokenSource(SettleTimeout);
                try
                {
                    await _queueActorInvoker.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                        actorId, ActorMethodNames.Nack, new ActorModels.NackRequest { LockId = lockId }, settleCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to nack lock {LockId} on queue {QueueId} after Consume stream ended: {Reason}",
                        lockId, start.QueueId, DescribeSettleFailure(ex, settleCts, $"Nack of lock {lockId}"));
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
            // Once per batch, with the exception: the settler then reports each lock in it.
            var reason = DescribeSettleFailure(ex, settleCts, $"AcknowledgeBatch of {lockIds.Count} lock(s)");
            _logger.LogWarning(ex, "Settle failed on Consume stream for queue {QueueId}: {Reason}", actorId, reason);
            return lockIds.Select(id => (id, "SETTLE_FAILED", (string?)reason)).ToList();
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
            var lockId = req.PayloadCase switch
            {
                ConsumeRequest.PayloadOneofCase.Ack => req.Ack.LockId,
                ConsumeRequest.PayloadOneofCase.Nack => req.Nack.LockId,
                _ => req.DeadLetter.LockId
            };
            return ("SETTLE_FAILED", DescribeSettleFailure(ex, settleCts, $"{req.PayloadCase} of lock {lockId}"));
        }
    }

    /// <summary>
    /// Why a settle call to the queue actor failed, for the client's SettleFailed and the log. Our own
    /// SettleTimeout expiring is named as such - its exception alone reads only "A task was canceled." -
    /// and anything else carries its exception type.
    /// </summary>
    private string DescribeSettleFailure(Exception ex, CancellationTokenSource settleCts, string operation) =>
        ex is OperationCanceledException && settleCts.IsCancellationRequested
            ? $"{operation} timed out after {SettleTimeout.TotalSeconds:0.###}s waiting for the queue actor"
            : $"{operation} failed: {ex.GetType().Name}: {ex.Message}";

    // The actor caps AcknowledgeBatch and ExtendLockBatch at 1000 locks a call.
    private const int MaxSettleBatchSize = 1000;

    private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// When a lock expiring at expiresAt (Unix seconds) is next due for renewal: min(remaining / 2, 10s)
    /// before it expires. This is Azure Service Bus's processor rule (ReceiverManager.CalculateRenewDelay)
    /// without its 0-200ms jitter. Service Bus jitters per-message timers so they don't all call the
    /// broker at once; here renewals are batched, and the queue actor runs one turn at a time, so each
    /// renewal's new expiry is already staggered by the turn it ran in. Jitter would only split locks
    /// that come due together into separate ExtendLockBatch calls. Renews each lock about once per TTL.
    /// </summary>
    internal static double RenewAt(double expiresAt)
    {
        var remaining = expiresAt - UnixNow();
        var buffer = Math.Min(remaining / 2, MaxRenewBufferSeconds);
        return expiresAt - Math.Max(buffer, 0);
    }

    private const double MaxRenewBufferSeconds = 10;

    /// <summary>
    /// Which of a Consume stream's locks get renewed. By default every delivered, unsettled one. With
    /// RENEW_STARTED only those a handler has started (a Started frame), and each only until MaxSeconds
    /// after it started (0 = no limit) - Service Bus's renewal of messages in a handler, up to
    /// MaxAutoLockRenewalDuration. A lock that isn't renewed runs out, and the sweep returns its item to
    /// the queue as a delivery attempt.
    /// </summary>
    private sealed record LockRenewalPolicy(bool StartedOnly, int MaxSeconds)
    {
        public bool Renews(OutstandingLock held, double now) =>
            !StartedOnly || (held.StartedAt is { } startedAt && (MaxSeconds == 0 || now - startedAt < MaxSeconds));
    }

    /// <summary>One delivered, unsettled lock: its expiry, its next renewal (see RenewAt), and when a handler started it.</summary>
    private sealed class OutstandingLock(double expiresAt)
    {
        private readonly object _gate = new();

        public double ExpiresAt { get; private set; } = expiresAt;
        public double RenewAt { get; private set; } = DaprMQGrpcService.RenewAt(expiresAt);
        public double? StartedAt { get; private set; }

        public void MarkStarted()
        {
            lock (_gate)
            {
                StartedAt ??= UnixNow();
            }
        }

        public void Renewed(double expiresAt)
        {
            lock (_gate)
            {
                ExpiresAt = expiresAt;
                RenewAt = DaprMQGrpcService.RenewAt(expiresAt);
            }
        }

        public void RetryAt(double at)
        {
            lock (_gate)
            {
                RenewAt = at;
            }
        }
    }

    /// <summary>Time until the next lock is due for renewal, or lapses unrenewed and frees its slot.</summary>
    private static TimeSpan UntilNextRenewal(ConcurrentDictionary<string, OutstandingLock> outstanding, LockRenewalPolicy renewal)
    {
        var now = UnixNow();
        var next = outstanding.Values.Select(l => renewal.Renews(l, now) ? l.RenewAt : l.ExpiresAt).DefaultIfEmpty(double.MaxValue).Min();
        return next == double.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromSeconds(Math.Max(0, next - now));
    }

    /// <summary>
    /// Renews the outstanding locks that are due (see RenewAt) and that the policy renews, to
    /// lockTtlSeconds from now, in batches of up to 1000, and schedules each one's next renewal from its
    /// new expiry. A lock the actor no longer has is dropped from the set: its item is already on its way
    /// back to the queue, and the client learns that when it tries to settle it. So is a lock the policy
    /// doesn't renew once it has lapsed, which frees its slot for a refill. A failed call retries those
    /// locks a second later.
    /// </summary>
    private async Task RenewDueLocksAsync(
        ActorId actorId, ConcurrentDictionary<string, OutstandingLock> outstanding, LockRenewalPolicy renewal, int lockTtlSeconds, CancellationToken cancellationToken)
    {
        var now = UnixNow();
        var due = new List<string>();
        foreach (var (lockId, held) in outstanding)
        {
            if (renewal.Renews(held, now))
            {
                if (held.RenewAt <= now)
                {
                    due.Add(lockId);
                }
            }
            else if (held.ExpiresAt <= now && outstanding.TryRemove(lockId, out _))
            {
                _logger.LogDebug("Lock {LockId} lapsed unrenewed on Consume stream for queue {QueueId}", lockId, actorId);
            }
        }

        foreach (var batch in due.Chunk(MaxSettleBatchSize))
        {
            try
            {
                var result = await _queueActorInvoker.InvokeMethodAsync<ActorModels.ExtendLockBatchRequest, ActorModels.ExtendLockBatchResponse>(
                    actorId,
                    ActorMethodNames.ExtendLockBatch,
                    new ActorModels.ExtendLockBatchRequest { LockIds = batch.ToList(), TtlSeconds = lockTtlSeconds },
                    cancellationToken);

                foreach (var renewed in result.Results)
                {
                    if (renewed.Outcome == "EXTENDED")
                    {
                        // Only if still outstanding: a settle may have removed it while the call ran.
                        if (outstanding.TryGetValue(renewed.LockId, out var held))
                        {
                            held.Renewed(renewed.NewExpiresAt > 0 ? renewed.NewExpiresAt : UnixNow() + lockTtlSeconds);
                        }
                    }
                    else
                    {
                        outstanding.TryRemove(renewed.LockId, out _);
                        _logger.LogWarning("Lock {LockId} lost on Consume stream for queue {QueueId}: {Outcome}", renewed.LockId, actorId, renewed.Outcome);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Lock renewal failed on Consume stream for queue {QueueId}", actorId);
                var retryAt = UnixNow() + 1;
                foreach (var lockId in batch)
                {
                    if (outstanding.TryGetValue(lockId, out var held))
                    {
                        held.RetryAt(retryAt);
                    }
                }
            }
        }
    }
}
