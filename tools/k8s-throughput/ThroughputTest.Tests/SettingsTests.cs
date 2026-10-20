namespace ThroughputTest.Tests;

public class SettingsTests
{
    private static Settings Parse(params (string Key, string Value)[] env) =>
        Settings.Parse(env.ToDictionary(e => e.Key, e => e.Value));

    [Fact]
    public void Defaults_AreTheTwentyReplicaRunOfTheLoadTests()
    {
        var settings = Parse(("QUEUE_ID", "tp-1"));

        Assert.Equal(100_000, settings.Messages);
        Assert.Equal(10, settings.Publishers);
        Assert.Equal(100, settings.PublishBatch);
        Assert.Equal(10, settings.Handlers);
        Assert.Equal(40, settings.Window);
        Assert.Equal(200, settings.ItemDelayMs);
        Assert.Equal(60, settings.LockTtlSeconds);
    }

    [Fact]
    public void ZeroIsAValue_NotUnset()
    {
        // The examples worker's EnvInt read 0 as unset, so WORKER_ITEM_DELAY_MS=0 silently ran at 200ms.
        var settings = Parse(("QUEUE_ID", "tp-1"), ("ITEM_DELAY_MS", "0"));

        Assert.Equal(0, settings.ItemDelayMs);
    }

    [Theory]
    [InlineData("MESSAGES", "lots")]
    [InlineData("HANDLERS", "0")]
    [InlineData("WINDOW", "-1")]
    [InlineData("ITEM_DELAY_MS", "-5")]
    public void AnInvalidValue_IsRejected_NamingTheVariable(string key, string value)
    {
        var error = Assert.Throws<ArgumentException>(() => Parse(("QUEUE_ID", "tp-1"), (key, value)));

        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void Workload_DefaultsToTheQueue()
    {
        Assert.Equal("queue", Parse(("QUEUE_ID", "tp-1")).Workload);
    }

    [Fact]
    public void SessionDefaults_AreAThousandSessionsOfAHundred_TenAtATimePerConsumer()
    {
        var settings = Parse(("QUEUE_ID", "tp-1"), ("WORKLOAD", "sessions"));

        Assert.Equal("sessions", settings.Workload);
        Assert.Equal(1000, settings.Sessions);
        Assert.Equal(100, settings.MessagesPerSession);
        Assert.Equal(10, settings.ConcurrentSessions);
        Assert.Equal(1, settings.Prefetch);
        Assert.Equal(30, settings.LeaseSeconds);
        // Not the server's default (the lease): a slot would sit on each drained session for 30s.
        Assert.Equal(1, settings.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public void AnUnknownWorkload_IsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => Parse(("QUEUE_ID", "tp-1"), ("WORKLOAD", "topics")));

        Assert.Contains("WORKLOAD", error.Message);
    }

    [Fact]
    public void Sessions_MustDivideTheMessagesEvenly()
    {
        // Every session gets the same number of messages, so the key's SESSIONSxPERSESSION is exact.
        var error = Assert.Throws<ArgumentException>(() =>
            Parse(("QUEUE_ID", "tp-1"), ("WORKLOAD", "sessions"), ("MESSAGES", "1000"), ("SESSIONS", "300")));

        Assert.Contains("SESSIONS", error.Message);
    }

    [Fact]
    public void QueueId_IsRequired()
    {
        var error = Assert.Throws<ArgumentException>(() => Parse());

        Assert.Contains("QUEUE_ID", error.Message);
    }
}
