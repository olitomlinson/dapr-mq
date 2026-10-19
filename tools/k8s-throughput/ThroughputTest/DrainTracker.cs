namespace ThroughputTest;

/// <summary>
/// Decides when the queue has drained from successive depth reads (ready + locked). It's drained once
/// publishing is done and <paramref name="confirmations"/> reads in a row are 0: the scaler caches
/// depth briefly, so a single 0 could be stale, and an expired lock can put an item back after a 0.
/// The drain time is the first 0 of that run of reads.
/// </summary>
public sealed class DrainTracker(int confirmations = 2)
{
    private int _zeros;
    private TimeSpan _firstZero;

    public bool Drained { get; private set; }
    public TimeSpan? DrainedAt { get; private set; }
    public double PeakDepth { get; private set; }

    public void Observe(double depth, TimeSpan sinceStart, bool publishingDone)
    {
        PeakDepth = Math.Max(PeakDepth, depth);
        if (Drained)
        {
            return;
        }

        if (!publishingDone || depth > 0)
        {
            _zeros = 0;
            return;
        }

        if (_zeros++ == 0)
        {
            _firstZero = sinceStart;
        }

        if (_zeros >= confirmations)
        {
            Drained = true;
            DrainedAt = _firstZero;
        }
    }
}
