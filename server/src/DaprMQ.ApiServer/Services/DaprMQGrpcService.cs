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
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error releasing session for queue {request.QueueId}, session {request.SessionId}");
            throw new RpcException(new Status(StatusCode.Internal, $"Internal error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Managed consume loop for exactly one session: claims it (any-available or targeted),
    /// streams delivered items back, accepts Ack/DeadLetter frames, and renews the lease on its
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
        var prefetchCount = start.PrefetchCount > 0 ? start.PrefetchCount : 10;
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

        // Reads Ack/DeadLetter frames from the client for the life of the stream. Cancels the
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
                        await _queueActorInvoker.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                            sessionActorId,
                            ActorMethodNames.Acknowledge,
                            new ActorModels.AcknowledgeRequest { LockId = req.Ack.LockId, LeaseId = leaseId },
                            cts.Token);
                        Interlocked.Decrement(ref outstanding);
                    }
                    else if (req.PayloadCase == ConsumeSessionRequest.PayloadOneofCase.DeadLetter)
                    {
                        await _queueActorInvoker.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                            sessionActorId,
                            ActorMethodNames.DeadLetter,
                            new ActorModels.DeadLetterRequest { LockId = req.DeadLetter.LockId, LeaseId = leaseId },
                            cts.Token);
                        Interlocked.Decrement(ref outstanding);
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
                if (capacity > 0)
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

                await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);
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
}
