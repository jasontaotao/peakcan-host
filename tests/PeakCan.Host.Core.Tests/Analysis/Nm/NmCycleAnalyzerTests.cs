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
}
