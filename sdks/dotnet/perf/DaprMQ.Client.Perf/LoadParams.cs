namespace DaprMQ.Client.Perf;

public static class LoadScenarios
{
    public const string Enqueue = "enqueue";
    public const string EnqueueBatch = "enqueue-batch";
    public const string DequeueAck = "dequeue-ack";
}

/// <summary>
/// One closed-loop load profile (P-01..P-03 in sdks/testing/PERFORMANCE_TESTS.md). More than one
/// <see cref="Concurrency"/> value makes it a ramp; <see cref="Queues"/> null = one queue per worker.
/// </summary>
public sealed record LoadParams(
    string Scenario,
    int[] Concurrency,
    int? Queues,
    int BatchSize,
    int DequeueCount,
    int PayloadBytes,
    int SeedPerQueue,
    int WarmupSeconds,
    int DurationSeconds)
{
    public string Id => Scenario switch
    {
        LoadScenarios.Enqueue => "P-01",
        LoadScenarios.EnqueueBatch => "P-02",
        LoadScenarios.DequeueAck => "P-03",
        _ => throw new InvalidOperationException($"Unknown load scenario '{Scenario}'."),
    };

    public int QueuesAt(int concurrency) => Queues ?? concurrency;

    /// <summary>Canonical form of the parameters; groups comparable runs across SDKs.</summary>
    public string Key => $"{Scenario}:c{string.Join(",", Concurrency)}/q{(Queues is { } q ? q.ToString() : "=c")}"
        + (Scenario == LoadScenarios.DequeueAck ? $"/k{DequeueCount}/seed{SeedPerQueue}" : $"/b{BatchSize}")
        + $"/{PayloadBytes}B/{WarmupSeconds}+{DurationSeconds}s";

    public bool Equals(LoadParams? other) => other is not null && Key == other.Key;

    public override int GetHashCode() => Key.GetHashCode();
}
