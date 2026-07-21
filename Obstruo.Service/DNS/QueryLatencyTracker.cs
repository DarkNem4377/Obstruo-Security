namespace Obstruo.Service.Dns;

/// <summary>
/// Rolling latency sampler for the local block-decision path (finding M1). The
/// v1.0.0 audit reported blocked names sometimes timing out before returning
/// NXDOMAIN; the block path is synchronous and in-memory, so this records how
/// long a blocked query actually takes to answer and exposes p50/p95 on the
/// dashboard, turning "feels slow" into a measured number.
///
/// Only locally-answered (blocked / health-probe) queries are recorded — upstream
/// forwarding latency is network-bound and not the product's SLA. Lock-guarded
/// over a fixed ring buffer: O(1) record, bounded memory, safe to call from the
/// DNS query threads.
/// </summary>
public sealed class QueryLatencyTracker
{
    private readonly double[] _samples;
    private int _count;      // number of valid samples (caps at capacity)
    private int _next;       // ring write cursor
    private readonly object _gate = new();

    public QueryLatencyTracker(int capacity = 2048)
    {
        if (capacity < 1) capacity = 1;
        _samples = new double[capacity];
    }

    public void Record(double milliseconds)
    {
        if (milliseconds < 0 || double.IsNaN(milliseconds)) return;
        lock (_gate)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }
    }

    /// <summary>Current p50/p95 over the retained window (both 0 when empty).</summary>
    public (double P50, double P95, int Count) Snapshot()
    {
        double[] copy;
        lock (_gate)
        {
            if (_count == 0) return (0, 0, 0);
            copy = new double[_count];
            Array.Copy(_samples, copy, _count);
        }

        Array.Sort(copy);
        return (Percentile(copy, 50), Percentile(copy, 95), copy.Length);
    }

    /// <summary>
    /// Nearest-rank percentile over an already-sorted ascending array. Pure and
    /// deterministic — unit-tested. <paramref name="p"/> in [0,100].
    /// </summary>
    internal static double Percentile(double[] sortedAsc, int p)
    {
        if (sortedAsc.Length == 0) return 0;
        if (sortedAsc.Length == 1) return sortedAsc[0];

        // Nearest-rank: rank = ceil(p/100 * N), clamped to [1, N].
        var rank = (int)Math.Ceiling(p / 100.0 * sortedAsc.Length);
        if (rank < 1) rank = 1;
        if (rank > sortedAsc.Length) rank = sortedAsc.Length;
        return sortedAsc[rank - 1];
    }
}
