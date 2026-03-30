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
}
