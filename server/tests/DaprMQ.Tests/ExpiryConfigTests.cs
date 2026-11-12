using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class ExpiryConfigTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var config = new ExpiryConfig();

        config.Validate();
        Assert.Equal(600, config.MinExpiryTtlSeconds);
        Assert.Equal(31_536_000, config.DefaultExpiryTtlSeconds);
        Assert.Equal(2_419_200, config.DeadLetterExpiryTtlSeconds);
        Assert.Equal(200, config.ExpirySweepBatchSize);
        Assert.False(config.DeadLetterOnExpiry);
    }

    public static TheoryData<ExpiryConfig> InvalidConfigs => new()
    {
        new ExpiryConfig { MinExpiryTtlSeconds = 0 },
        new ExpiryConfig { MinExpiryTtlSeconds = ExpiryConfig.MaxExpiryTtlSeconds + 1 },
        new ExpiryConfig { MinExpiryTtlSeconds = 3600, DefaultExpiryTtlSeconds = 3599 },
        new ExpiryConfig { DefaultExpiryTtlSeconds = ExpiryConfig.MaxExpiryTtlSeconds + 1 },
        new ExpiryConfig { MinExpiryTtlSeconds = 3600, DeadLetterExpiryTtlSeconds = 3599 },
        new ExpiryConfig { DeadLetterExpiryTtlSeconds = ExpiryConfig.MaxExpiryTtlSeconds + 1 },
        new ExpiryConfig { ExpirySweepBatchSize = 0 },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void Validate_OutOfRange_Throws(ExpiryConfig config)
    {
        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Validate_EverythingAtTheBounds_Passes()
    {
        new ExpiryConfig { MinExpiryTtlSeconds = 1, DefaultExpiryTtlSeconds = 1, DeadLetterExpiryTtlSeconds = 1 }.Validate();
        new ExpiryConfig
        {
            MinExpiryTtlSeconds = ExpiryConfig.MaxExpiryTtlSeconds,
            DefaultExpiryTtlSeconds = ExpiryConfig.MaxExpiryTtlSeconds,
            DeadLetterExpiryTtlSeconds = ExpiryConfig.MaxExpiryTtlSeconds
        }.Validate();
    }
}
