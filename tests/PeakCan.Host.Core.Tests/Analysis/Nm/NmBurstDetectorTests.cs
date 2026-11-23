using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.4 / §7.5.3.1：快发检测只有一个实现。10 帧 = 9 个间隔，
/// 真实 trace §14.3 的跨度为 181.497ms（本类夹具为 181.3ms），不是「10 × 20ms = 200ms」。
/// </summary>
public class NmBurstDetectorTests
{
    private static readonly NmEffectiveTiming Geely = new(
        CycleMs: 500, TimeoutMs: 2000, WaitBusSleepMs: 2000,
        RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
        ImmediateCycleMs: 20, ImmediateCount: 10,
        StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1);

    [Fact]
    public void Detect_RealTraceBurst_IsCompliant()
    {
        // 与真实 trace 第二段同形：10 帧，9 个间隔 19.0–21.0ms（合计 181.3ms）。
        var ts = new double[10];
        ts[0] = 12.677179;
        var intervals = new[] { 19.5, 20.0, 20.5, 19.0, 21.0, 20.2, 19.8, 20.4, 20.9 };
        for (var i = 1; i < ts.Length; i++) ts[i] = ts[i - 1] + intervals[i - 1] / 1000.0;

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.True(d.IsCompliant);
        Assert.Equal(10, d.FrameCount);
        Assert.Equal(9, d.IntervalCount);
        Assert.Equal("immediate", d.Detector);
        Assert.False(d.IsIndeterminate);
    }

    [Fact]
    public void Detect_NineFrames_IsCandidateButNotCompliant()
    {
        var ts = Enumerable.Range(0, 9).Select(i => i * 0.020).ToArray();

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void Detect_SlowIntervals_IsNotCandidate()
    {
        var ts = Enumerable.Range(0, 10).Select(i => i * 0.5).ToArray();

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.False(d.IsCandidate);
        Assert.Equal(0, d.FrameCount);
    }

    [Fact]
    public void Detect_IntervalOutOfTolerance_IsCandidateButNotCompliant()
    {
        // 10 帧但有一个间隔 30ms（> 20×1.1=22ms）→ 检测得到候选，判不合规。
        var ts = new double[10];
        ts[0] = 0;
        for (var i = 1; i < ts.Length; i++) ts[i] = ts[i - 1] + (i == 5 ? 0.030 : 0.020);

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void Detect_NoImmediateCycle_FallsBackToCycleRatio()
    {
        // ImmediateCycleMs 缺失：回退 0.5×cycle 检测，标 cycle_ratio_fallback + inferred。
        var timing = Geely with { ImmediateCycleMs = 0 };
        var ts = Enumerable.Range(0, 10).Select(i => i * 0.100).ToArray();

        var d = NmBurstDetector.Detect(ts, timing);

        Assert.True(d.IsCandidate);
        Assert.Equal("cycle_ratio_fallback", d.Detector);
        // 回退路径不得判「合规」——它证明不了符合 N_ImmediateNM_TIMES。
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void Detect_NoCycleAtAll_ReturnsIndeterminate()
    {
        var timing = Geely with { ImmediateCycleMs = 0, CycleMs = 0 };
        var ts = new double[] { 0, 0.02, 0.04 };

        var d = NmBurstDetector.Detect(ts, timing);

        Assert.True(d.IsIndeterminate);
        Assert.False(d.IsCandidate);
    }

    [Fact]
    public void Detect_FallbackIntervalExactlyHalfCycle_IsNotCandidate()
    {
        // 回退路径沿用 M1 的严格 <：恰为 0.5×cycle（250ms）的间隔算断点，不能凑成候选。
        var timing = Geely with { ImmediateCycleMs = 0 };
        var ts = Enumerable.Range(0, 10).Select(i => i * 0.250).ToArray();

        var d = NmBurstDetector.Detect(ts, timing);

        Assert.False(d.IsCandidate);
    }

    [Fact]
    public void Detect_DuplicateTimestamp_IsIgnoredNotCounted()
    {
        // 重复帧是同一时刻：不构成间隔，也不打断连续段。5 帧去重后 4 个真实时刻 → 3 个间隔。
        var ts = new double[] { 0, 0, 0.020, 0.040, 0.060 };

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.Equal(4, d.FrameCount);
        Assert.Equal(3, d.IntervalCount);
    }

    [Fact]
    public void Detect_AllDuplicates_IsNotCandidate()
    {
        // 全是同一时刻：没有任何真实间隔，不能制造快发候选。
        var ts = new double[] { 0, 0, 0, 0, 0 };

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.False(d.IsCandidate);
        Assert.Equal(0, d.FrameCount);
    }

    [Fact]
    public void Detect_DuplicateInsideRun_ReportsCorrectSpanAndFrameCount()
    {
        // 回归：重复帧夹在连续段中段。若在循环里跳过重复帧而 runStart/runCount 仍按原数组下标，
        // 下标会错位，end 会取到错误帧（0.04 而非 0.06）。
        var ts = new double[] { 0, 0.020, 0.020, 0.040, 0.060 };

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.Equal(4, d.FrameCount);
        Assert.Equal(3, d.IntervalCount);
        Assert.Equal(0.0, d.Start, 9);
        Assert.Equal(0.060, d.End, 9);
    }

    [Fact]
    public void Detect_DuplicateInsideCompliantBurst_IsStillCompliant()
    {
        // 10 个真实帧（20ms 等距）中插一个重复帧：同一时刻的两帧只算一次，
        // 真实间隔仍全部为 20ms，应判合规。
        var distinct = Enumerable.Range(0, 10).Select(i => i * 0.020).ToArray();
        var ts = distinct.Take(5).Concat(new[] { distinct[4] }).Concat(distinct.Skip(5)).ToArray();

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.Equal(10, d.FrameCount);
        Assert.Equal(9, d.IntervalCount);
        Assert.True(d.IsCompliant);
    }

    [Fact]
    public void DetectInEpisode_BurstCutByWindowEnd_IsIndeterminate()
    {
        // 快发段延伸到 episode 结束仍未停 → 可能被窗口切断。
        var ts = Enumerable.Range(0, 5).Select(i => i * 0.020).ToArray();

        var d = NmBurstDetector.DetectInEpisode(ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely);

        Assert.True(d.IsCandidate);
        Assert.True(d.IsIndeterminate);
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void DetectInEpisode_TenInToleranceFramesCutAtEpisodeEnd_IsIndeterminateOnly()
    {
        // 截断是唯一让 IsCompliant=false 的原因：10 帧（FrameCount == ImmediateCount），
        // 9 个间隔全部落在 20ms×(1±10%) = 18–22ms 内，且末帧正好压在 episodeEnd 上。
        var intervals = new[] { 19.021, 20.0, 20.5, 19.0, 21.0, 20.2, 19.8, 20.4, 20.996 };
        var ts = new double[10];
        ts[0] = 0;
        for (var i = 1; i < ts.Length; i++) ts[i] = ts[i - 1] + intervals[i - 1] / 1000.0;

        var d = NmBurstDetector.DetectInEpisode(ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely);

        Assert.True(d.IsCandidate);
        Assert.True(d.IsIndeterminate);
        Assert.False(d.IsCompliant);
        Assert.Equal(10, d.FrameCount);
        Assert.Equal(9, d.IntervalCount);
    }
}
