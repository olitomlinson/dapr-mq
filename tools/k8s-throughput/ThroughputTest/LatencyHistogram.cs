namespace ThroughputTest;

/// <summary>A latency distribution in the shape of result.schema.json's `distribution`.</summary>
public sealed record Distribution(long Count, double Mean, double P50, double P95, double P99, double Max);

/// <summary>A histogram's state, small enough to send in a stats message: only non-empty buckets.</summary>
public sealed record HistogramSnapshot(long Count, double Sum, double Max, Dictionary<int, long> Buckets);

/// <summary>
/// Delivery latencies (ms) in buckets that grow by 10%, so each consumer pod keeps a fixed-size summary
/// and the run can merge every pod's into one. Count, mean and max are exact; a percentile is the upper
/// bound of its bucket (capped at the max), so within 10% of the true value.
/// </summary>
public sealed class LatencyHistogram
{
    private const double Growth = 1.1;
    private readonly object _gate = new();
    private readonly Dictionary<int, long> _buckets = new();
    private long _count;
    private double _sum;
    private double _max;

    public void Record(double ms)
    {
        var value = Math.Max(ms, 0);
        var bucket = value <= 1 ? 0 : (int)Math.Ceiling(Math.Log(value) / Math.Log(Growth));
        lock (_gate)
        {
            _buckets[bucket] = _buckets.GetValueOrDefault(bucket) + 1;
            _count++;
            _sum += value;
            _max = Math.Max(_max, value);
        }
    }

    public HistogramSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HistogramSnapshot(_count, _sum, _max, new Dictionary<int, long>(_buckets));
        }
    }

    public static LatencyHistogram Merge(IEnumerable<HistogramSnapshot> snapshots)
    {
        var merged = new LatencyHistogram();
        foreach (var snapshot in snapshots)
        {
            merged._count += snapshot.Count;
            merged._sum += snapshot.Sum;
            merged._max = Math.Max(merged._max, snapshot.Max);
            foreach (var (bucket, count) in snapshot.Buckets)
            {
                merged._buckets[bucket] = merged._buckets.GetValueOrDefault(bucket) + count;
            }
        }
        return merged;
    }

    public Distribution Summary()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return new Distribution(0, 0, 0, 0, 0, 0);
            }

            var ordered = _buckets.OrderBy(b => b.Key).ToList();
            double Percentile(double p)
            {
                var rank = (long)Math.Ceiling(p * _count);
                long seen = 0;
                foreach (var (bucket, count) in ordered)
                {
                    seen += count;
                    if (seen >= rank)
                    {
                        return Math.Min(Math.Pow(Growth, bucket), _max);
                    }
                }
                return _max;
            }

            return new Distribution(_count, _sum / _count, Percentile(0.50), Percentile(0.95), Percentile(0.99), _max);
        }
    }
}
