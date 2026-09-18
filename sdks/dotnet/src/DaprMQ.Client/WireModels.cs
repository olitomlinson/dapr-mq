using System.Text.Json;

namespace DaprMQ.Client;

// Mirrors DaprMQ.ApiServer.Models.ApiModels.cs, client side. Field names/casing match the
// server's camelCase JSON convention (System.Text.Json, PropertyNamingPolicy.CamelCase).

internal record EnqueueResponseWire(bool Success, string Message, int ItemsEnqueued, int ItemsDeduplicated = 0);

internal record DequeueLockedItemWire(JsonElement Item, int Priority, string LockId, double LockExpiresAt);

internal record DequeueLockedResponseWire(List<DequeueLockedItemWire> Items, bool Locked, string? Message = null);

internal record LockedResponseWire(string? Message, double? LockExpiresAt);

internal record AcknowledgeResponseWire(bool Success, string Message, int ItemsAcknowledged = 0, string? ErrorCode = null);

internal record ExtendLockResponseWire(long NewExpiresAt, string LockId);

internal record DeadLetterResponseWire(bool Success, string Message, string? ErrorCode = null, string? DlqId = null);

internal record ErrorResponseWire(string Message, bool Success = false);

internal record AcceptSessionResponseWire(string SessionId, string LeaseId, double LeaseExpiresAt);

internal record RenewSessionLeaseResponseWire(double NewExpiresAt);

internal record ReleaseSessionResponseWire(bool Success);
