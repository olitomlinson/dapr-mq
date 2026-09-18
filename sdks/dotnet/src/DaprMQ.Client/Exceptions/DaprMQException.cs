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
