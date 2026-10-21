namespace ThroughputTest;

/// <summary>One session of a sessions run: how many messages it gets, and when it's published.</summary>
public sealed record PlannedSession(int Index, int Messages, double PublishAtSeconds);

/// <summary>
/// The sessions a run publishes. Without jitter every session gets MessagesPerSession, all published at
/// the start. SessionLengthJitterPercent draws each length within that percentage of the mean, then
/// rounds them to add up to MESSAGES exactly; PublishJitterSeconds gives each session a random publish
/// time in that window. Fixed seeds make the plan the same for every run of the same settings, so runs
/// that share a key share an ideal time. The two draws use separate seeds, so turning one on doesn't
/// change the other.
/// </summary>
public static class SessionPlan
{
    private const int LengthSeed = 20261021;
    private const int PublishSeed = LengthSeed + 1;

    public static IReadOnlyList<PlannedSession> Build(Settings s)
    {
        var lengths = Lengths(s.Sessions, s.Messages, s.SessionLengthJitterPercent / 100.0);
        var publishRandom = new Random(PublishSeed);
        return Enumerable.Range(0, s.Sessions)
            .Select(i => new PlannedSession(i, lengths[i], s.PublishJitterSeconds > 0 ? publishRandom.NextDouble() * s.PublishJitterSeconds : 0))
            .ToList();
    }

    private static int[] Lengths(int sessions, int messages, double jitter)
    {
        var random = new Random(LengthSeed);
        var weights = Enumerable.Range(0, sessions).Select(_ => 1 + jitter * (2 * random.NextDouble() - 1)).ToArray();
        var total = weights.Sum();
        var exact = weights.Select(w => w / total * messages).ToArray();
        var lengths = exact.Select(e => (int)Math.Floor(e)).ToArray();

        // Floors fall short of the total; the sessions that lost the most to rounding make it up.
        foreach (var i in Enumerable.Range(0, sessions).OrderByDescending(i => exact[i] - lengths[i]).Take(messages - lengths.Sum()))
        {
            lengths[i]++;
        }

        return lengths;
    }

    /// <summary>
    /// The least time any schedule needs: the work spread over every slot that can run a session, or the
    /// session that finishes last at best (published at its offset, then handled one message after
    /// another), whichever is later. Sessions can't be split, so a real drain can't quite reach it.
    /// </summary>
    public static double? IdealSeconds(Settings s, IReadOnlyList<PlannedSession> plan)
    {
        if (s.ItemDelayMs <= 0)
        {
            return null;
        }

        var perMessage = s.ItemDelayMs / 1000.0;
        var slots = Math.Min(s.Replicas * s.ConcurrentSessions, s.Sessions);
        return Math.Max(s.Messages * perMessage / slots, plan.Max(p => p.PublishAtSeconds + p.Messages * perMessage));
    }
}
