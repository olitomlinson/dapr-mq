namespace DaprMQ.Client.Perf;

public static class PublishSchedule
{
    /// <summary>
    /// Delay before each of a session's messages: the first after a random 0..jitter start offset
    /// (so sessions don't publish in lockstep), each later one after interval + random 0..jitter.
    /// </summary>
    public static int[] Delays(int messages, int intervalMs, int jitterMs, Random random) =>
        Enumerable.Range(0, messages)
            .Select(i => (i == 0 ? 0 : intervalMs) + (jitterMs > 0 ? random.Next(jitterMs + 1) : 0))
            .ToArray();
}
