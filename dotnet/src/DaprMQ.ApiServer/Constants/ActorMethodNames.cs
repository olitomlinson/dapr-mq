namespace DaprMQ.ApiServer.Constants;

/// <summary>
/// Actor method names for nonremoting invocations.
/// </summary>
public static class ActorMethodNames
{
    public const string Push = "Push";
    public const string Pop = "Pop";
    public const string PopWithAck = "PopWithAck";
    public const string Acknowledge = "Acknowledge";
    public const string ExtendLock = "ExtendLock";
    public const string DeadLetter = "DeadLetter";
    public const string InitializeHttpSink = "InitializeHttpSink";
    public const string UninitializeHttpSink = "UninitializeHttpSink";
    public const string InitializeDaprPubSubSink = "InitializeDaprPubSubSink";
    public const string UninitializeDaprPubSubSink = "UninitializeDaprPubSubSink";

    public const string TestUnsafeUnload = "TestUnsafeUnload";

    public const string Publish = "Publish";
    public const string Subscribe = "Subscribe";
    public const string Unsubscribe = "Unsubscribe";
    public const string ListSubscribers = "ListSubscribers";
    public const string GetPublishStatus = "GetPublishStatus";
    public const string ResetCircuitBreaker = "ResetCircuitBreaker";
    public const string GetCircuitBreakerStatus = "GetCircuitBreakerStatus";
}
