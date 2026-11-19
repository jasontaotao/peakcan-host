using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmCycleAnalyzerTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];
    private static readonly string[] SenderIds = ["0x500"];

    private static NmMessageDef Def(int? cycle = 500, int? timeout = 2000)
        => new("0x500", false, "NM", "A", 8, true, TestReasons, cycle, timeout);

    private static NmEpisode[] OneEpisode(double start, double end)
        => new[] { new NmEpisode(0, start, end, "active", null, SenderIds, false, false) };

    [Fact]
    public void Analyze_RegularIntervals_ProducesMedianAndNoDeviation()
    {
        var times = Enumerable.Range(0, 10).Select(i => i * 0.5).ToArray();

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 4.5));

        Assert.Equal(500, stats.MedianMs);
        Assert.Equal(0, stats.DeviationCount);
        Assert.Equal(9, stats.IntervalCount);
        Assert.Equal(500, stats.ExpectedMs);
        Assert.Equal(100, stats.ToleranceMs); // max(0.2*500, 30)
    }

    [Fact]
    public void Analyze_FewerThanFiveIntervals_ReportsInsufficientSamplesOnly()
    {
        var times = new[] { 0.0, 0.5, 1.0, 1.5 };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 1.5));

        Assert.Equal(3, stats.IntervalCount);
        Assert.Equal(0, stats.DeviationCount);
        Assert.Null(stats.MedianMs);
    }

    [Fact]
    public void Analyze_ExcludesIntervalsCrossingEpisodeBoundary()
    {
        var times = new[] { 0.0, 0.5, 1.0, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0 };
        var episodes = new[]
        {
            new NmEpisode(0, 0, 1.0, "active", null, SenderIds, false, false),
            new NmEpisode(1, 5.0, 8.0, "active", null, SenderIds, false, false),
        };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), episodes);

        // 9 个间隔：ep1 内 2 个 + 跨边界 1 个（剔除）+ ep2 内 6 个 = 8 个有效。
        Assert.Equal(8, stats.IntervalCount);
        Assert.Equal(500, stats.MedianMs);
        Assert.Equal(0, stats.DeviationCount);
    }

    [Fact]
    public void Analyze_DeviationAboveFivePercent_ReportsCount()
    {
        // 2.0→2.7 的间隔是 0.7s，超出 500ms ± 100ms 的容差；0.6s 恰好等于容差边界，
        // 用 `>` 判据时不算偏差，所以这里必须用明显越界的值。
        var times = new[] { 0.0, 0.5, 1.0, 1.5, 2.0, 2.7, 3.2, 3.7, 4.2, 4.7 };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 4.7));

        Assert.True(stats.DeviationCount > 0);
    }

    [Fact]
    public void Analyze_NoExpectedCycle_StillProducesMedian()
    {
        var times = Enumerable.Range(0, 10).Select(i => i * 0.5).ToArray();

        var stats = NmCycleAnalyzer.Analyze(times, Def(cycle: null), OneEpisode(0, 4.5));

        Assert.Equal(500, stats.MedianMs);
        Assert.Null(stats.ExpectedMs);
        Assert.Equal(0, stats.DeviationCount);
    }

    [Fact]
    public void Analyze_ThreeConsecutiveDeviations_ReportsRunAndSignificance()
    {
        // 2.0→2.7→3.4→4.1 是连续 3 个 0.7s 间隔，均超出 500ms ± 100ms 容差。
        var times = new[] { 0.0, 0.5, 1.0, 1.5, 2.0, 2.7, 3.4, 4.1, 4.6 };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 4.6));

        Assert.Equal(8, stats.IntervalCount);
        Assert.Equal(3, stats.DeviationCount);
        Assert.Equal(3, stats.ConsecutiveDeviationCount);
        Assert.True(NmCycleAnalyzer.IsDeviationSignificant(stats));
    }

    [Fact]
    public void Analyze_OneDeviationInTenIntervals_IsSignificantByRatio()
    {
        // 10 个间隔中只有最后 1 个偏大：1 * 20 = 20 > 10，占比判据成立。
        var times = new[] { 0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.2 };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 5.2));

        Assert.Equal(10, stats.IntervalCount);
        Assert.Equal(1, stats.DeviationCount);
        Assert.Equal(1, stats.ConsecutiveDeviationCount);
        Assert.True(NmCycleAnalyzer.IsDeviationSignificant(stats));
    }

    [Fact]
    public void Analyze_FourIsolatedDeviationsInHundredIntervals_NotSignificant()
    {
        // 100 个间隔中 4 个孤立偏差：4 * 20 = 80 不大于 100，且最长连续为 1。
        var times = new List<double> { 0.0 };
        for (int i = 1; i <= 100; i++)
        {
            var step = i is 10 or 30 or 50 or 70 ? 0.7 : 0.5;
            times.Add(times[^1] + step);
        }

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, times[^1]));

        Assert.Equal(100, stats.IntervalCount);
        Assert.Equal(4, stats.DeviationCount);
        Assert.Equal(1, stats.ConsecutiveDeviationCount);
        Assert.False(NmCycleAnalyzer.IsDeviationSignificant(stats));
    }

    [Fact]
    public void Analyze_ExcludesCrossBoundaryInterval_WhenTimeoutNotFiltering()
    {
        // timeout 为 null 时，跨 episode 边界的 2.0s 间隔不会被超时阈值剔除，
        // 这里单独验证「跨边界」判据本身生效。
        var times = new[] { 0.0, 0.5, 1.0, 3.0, 3.5, 4.0 };
        var episodes = new[]
        {
            new NmEpisode(0, 0, 1.0, "active", null, SenderIds, false, false),
            new NmEpisode(1, 3.0, 4.0, "active", null, SenderIds, false, false),
        };

        var stats = NmCycleAnalyzer.Analyze(times, Def(cycle: 500, timeout: null), episodes);

        // 5 个原始间隔，1.0→3.0 跨边界被剔除，只剩 4 个。
        Assert.Equal(4, stats.IntervalCount);
        Assert.Equal(0, stats.ConsecutiveDeviationCount);
    }
}
