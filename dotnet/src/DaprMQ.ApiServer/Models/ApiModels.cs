using System.Text.Json;

namespace DaprMQ.ApiServer.Models;

// Request models
public record ApiPushRequest(
    JsonElement Item,
    int Priority = 1
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
    string Message
);

public record ApiPopResponse(
    object Item
);

public record ApiPopWithAckResponse(
    object Item,
    bool Locked,
    string? LockId,
    double? LockExpiresAt,
    string? Message
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
    string? DlqActorId = null
);
