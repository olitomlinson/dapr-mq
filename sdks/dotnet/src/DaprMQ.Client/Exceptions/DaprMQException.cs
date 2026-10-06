namespace DaprMQ.Client.Exceptions;

public class DaprMQException : Exception
{
    public string? ErrorCode { get; }

    public DaprMQException(string message, string? errorCode = null) : base(message)
    {
        ErrorCode = errorCode;
    }
}

public class LockNotFoundException : DaprMQException
{
    public LockNotFoundException(string message) : base(message, "LOCK_NOT_FOUND") { }
}

public class LockExpiredException : DaprMQException
{
    public LockExpiredException(string message) : base(message, "LOCK_EXPIRED") { }
}

public class ActorNotFoundException : DaprMQException
{
    public ActorNotFoundException(string message) : base(message, "ACTOR_NOT_FOUND") { }
}

public class ValidationException : DaprMQException
{
    public ValidationException(string message) : base(message, "VALIDATION_ERROR") { }
}

public class SessionNotFoundException : DaprMQException
{
    public SessionNotFoundException(string message) : base(message, "SESSION_NOT_FOUND") { }
}

public class SessionLockedException : DaprMQException
{
    public SessionLockedException(string message) : base(message, "SESSION_LOCKED") { }
}

public class SessionLeaseExpiredException : DaprMQException
{
    public SessionLeaseExpiredException(string message) : base(message, "SESSION_LEASE_EXPIRED") { }
}

public class InvalidLeaseIdException : DaprMQException
{
    public InvalidLeaseIdException(string message) : base(message, "INVALID_LEASE_ID") { }
}

public class SessionActorUnavailableException : DaprMQException
{
    public SessionActorUnavailableException(string message) : base(message, "SESSION_ACTOR_UNAVAILABLE") { }
}

public class NoSessionsAvailableException : DaprMQException
{
    public NoSessionsAvailableException(string message) : base(message, "NO_SESSIONS_AVAILABLE") { }
}

/// <summary>
/// Thrown by <see cref="IDaprMQClient.ConsumeSessionAsync"/> when a session was successfully
/// claimed but its lease could not be maintained afterwards (server sent a terminal SessionLost
/// frame) - distinct from a claim that never succeeded in the first place.
/// </summary>
public class SessionLostException : DaprMQException
{
    public SessionLostException(string message) : base(message, "SESSION_LOST") { }
}

/// <summary>
/// The operation was certainly not performed - DaprMQ couldn't serve it - and retrying ran out of
/// <see cref="DaprMQRetryOptions.Timeout"/>. Always safe to repeat later.
/// </summary>
public class DaprMQUnavailableException : DaprMQException
{
    public DaprMQUnavailableException(string message, string operation, string queueId)
        : base(message, "UNAVAILABLE")
    {
        Operation = operation;
        QueueId = queueId;
    }

    public string Operation { get; }

    public string QueueId { get; }
}

/// <summary>
/// The operation may or may not have been performed (e.g. the connection broke after it was sent),
/// and it isn't safe to repeat automatically. See sdks/testing/RETRIES_AND_READINESS.md for what to
/// do per operation; an Enqueue whose items all carry an IdempotencyKey is retried instead.
/// </summary>
public class DeliveryUnknownException : DaprMQException
{
    public DeliveryUnknownException(string message, string operation, string queueId, IReadOnlyList<string?>? idempotencyKeys = null)
        : base(message, "DELIVERY_UNKNOWN")
    {
        Operation = operation;
        QueueId = queueId;
        IdempotencyKeys = idempotencyKeys ?? [];
    }

    public string Operation { get; }

    public string QueueId { get; }

    /// <summary>For Enqueue: each item's key, in order (null where the item had none).</summary>
    public IReadOnlyList<string?> IdempotencyKeys { get; }
}
