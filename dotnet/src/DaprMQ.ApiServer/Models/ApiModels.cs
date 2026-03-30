using System.Text.Json;

namespace DaprMQ.ApiServer.Models;

// Request models
public record ApiPushRequest(
    List<ApiPushItem> Items
);

public record ApiPushItem(
    JsonElement Item,
    int Priority = 1,
    ApiSinkConfig? Sink = null
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
    int ItemsPushed
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
    double LockExpiresAt,
    ApiSinkConfig? Sink = null
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

public record ApiRegisterDaprPubSubSinkRequest(
    string PubSubName,
    string Topic,
    bool RawPayload = false,
    int MaxConcurrency = 5,
    int LockTtlSeconds = 30
);

public record ApiRegisterDaprPubSubSinkResponse(
    bool Success,
    string Message,
    string? DaprPubSubSinkActorId = null
);

public record ApiUnregisterDaprPubSubSinkResponse(
    bool Success,
    string Message
);

public record ApiSinkConfig(
    ApiDaprPubSubSinkConfig? DaprPubSub
);

public record ApiDaprPubSubSinkConfig(
    Dictionary<string, string>? Metadata
);
