using Obstruo.Service.Dns;

namespace Obstruo.Tests;

public class QueryLatencyTrackerTests
{
    // ── Percentile math ───────────────────────────────────────────────────────

    [Fact]
    public void Percentile_NearestRank_OverKnownSet()
    {
        double[] s = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]; // sorted ascending

        Assert.Equal(5, QueryLatencyTracker.Percentile(s, 50));   // ceil(0.5*10)=5 → s[4]=5
        Assert.Equal(10, QueryLatencyTracker.Percentile(s, 95));  // ceil(0.95*10)=10 → s[9]=10
        Assert.Equal(10, QueryLatencyTracker.Percentile(s, 100));
        Assert.Equal(1, QueryLatencyTracker.Percentile(s, 1));    // ceil(0.01*10)=1 → s[0]=1
    }

    [Fact]
    public void Percentile_EmptyAndSingle()
    {
        Assert.Equal(0, QueryLatencyTracker.Percentile([], 95));
        Assert.Equal(42, QueryLatencyTracker.Percentile([42], 50));
        Assert.Equal(42, QueryLatencyTracker.Percentile([42], 95));
    }

    // ── Tracker behaviour ─────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_EmptyTracker_IsAllZero()
    {
        var (p50, p95, count) = new QueryLatencyTracker().Snapshot();
        Assert.Equal(0, p50);
        Assert.Equal(0, p95);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Record_ThenSnapshot_ComputesPercentiles()
    {
        var t = new QueryLatencyTracker();
        for (int i = 1; i <= 100; i++) t.Record(i);

        var (p50, p95, count) = t.Snapshot();
        Assert.Equal(100, count);
        Assert.Equal(50, p50);
        Assert.Equal(95, p95);
    }

    [Fact]
    public void Record_RingBuffer_RetainsOnlyLastCapacity()
    {
        var t = new QueryLatencyTracker(capacity: 10);
        // 100 samples but capacity 10 → only the last 10 (91..100) are retained.
        for (int i = 1; i <= 100; i++) t.Record(i);

        var (_, _, count) = t.Snapshot();
        Assert.Equal(10, count);
        Assert.Equal(91, QueryLatencyTracker.Percentile([91, 92, 93, 94, 95, 96, 97, 98, 99, 100], 1));
    }

    [Fact]
    public void Record_IgnoresNegativeAndNaN()
    {
        var t = new QueryLatencyTracker();
        t.Record(-1);
        t.Record(double.NaN);
        Assert.Equal(0, t.Snapshot().Count);

        t.Record(5);
        Assert.Equal(1, t.Snapshot().Count);
    }
}
