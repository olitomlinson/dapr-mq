using System.Text.Json;

namespace DaprMQ.ApiServer.Models;

// Request models
public record ApiPushRequest(
    List<ApiPushItem> Items
);

public record ApiPushItem(
    JsonElement Item,
    int Priority = 1,
    string? IdempotencyKey = null
);

public record ApiAcknowledgeRequest(
    string LockId
);

public record ApiExtendLockRequest(
    string LockId,
    int AdditionalTtlSeconds = 30
);

// Response models
public record ApiPushResponse(
    bool Success,
    string Message,
    int ItemsPushed,
    int ItemsDeduplicated = 0
);

public record ApiPopResponse(
    List<ApiPopItem> Items
);

public record ApiPopItem(
    object Item,
    int Priority
);

public record ApiPopWithAckResponse(
    List<ApiPopWithAckItem> Items,
    bool Locked,
    string? Message = null
);

public record ApiPopWithAckItem(
    object Item,
    int Priority,
    string LockId,
    double LockExpiresAt
);

public record ApiAcknowledgeResponse(
    bool Success,
    string Message,
    int ItemsAcknowledged = 0,
    string? ErrorCode = null
);

public record ApiErrorResponse(
    string Message,
    bool Success = false
);

public record ApiLockedResponse(
    string? Message,
    double? LockExpiresAt
);

public record ApiExtendLockResponse(
    long NewExpiresAt,
    string LockId
);

public record ApiDeadLetterRequest(
    string LockId
);

public record ApiDeadLetterResponse(
    bool Success,
    string Message,
    string? ErrorCode = null,
    string? DlqId = null
);

public record ApiRegisterHttpSinkRequest(
    string Url,
    int MaxConcurrency = 5,
    int LockTtlSeconds = 30
);

public record ApiRegisterHttpSinkResponse(
    bool Success,
    string Message,
    string? HttpSinkActorId = null
);

public record ApiUnregisterHttpSinkResponse(
    bool Success,
    string Message
);

// Topic (pub/sub) models

public record ApiPublishRequest(
    List<ApiPushItem> Items
);

public record ApiPublishResponse(
    bool Accepted,
    string PublishId,
    long Sequence
);

public record ApiSubscribeRequest(
    ApiRegisterHttpSinkRequest? HttpSink = null,
    bool? DedupEnabled = null
);

public record ApiSubscribeResponse(
    bool Success,
    string QueueActorId
);

public record ApiUnsubscribeResponse(
    bool Success
);

public record ApiListSubscribersResponse(
    List<string> SubscriberIds
);

public record ApiPublishStatusResponse(
    bool Complete,
    List<string> TargetSubscriberIds,
    List<string> DeliveredSubscriberIds
);

public record ApiResetCircuitBreakerResponse(
    bool Success
);

public record ApiCircuitBreakerStatusResponse(
    int ConsecutiveFailures,
    double? FirstFailureAt,
    double? NextRetryAt,
    bool Blacklisted
);
