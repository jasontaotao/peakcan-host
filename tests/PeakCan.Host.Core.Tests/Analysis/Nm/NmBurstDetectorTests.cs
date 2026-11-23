using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.4 / §7.5.3.1：快发检测只有一个实现。10 帧 = 9 个间隔，
/// 跨度 181.5ms（真实 trace §14.3），不是「10 × 20ms = 200ms」。
/// </summary>
public class NmBurstDetectorTests
{
    private static readonly NmEffectiveTiming Geely = new(
        CycleMs: 500, TimeoutMs: 2200, WaitBusSleepMs: 2000,
        RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
        ImmediateCycleMs: 20, ImmediateCount: 10,
        StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1);

    [Fact]
    public void Detect_RealTraceBurst_IsCompliant()
    {
        // 真实 trace 第二段：10 帧，9 个间隔 19.021–20.996ms。
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
    public void DetectInEpisode_BurstCutByWindowEnd_IsIndeterminate()
    {
        // 快发段延伸到 episode 结束仍未停 → 可能被窗口切断。
        var ts = Enumerable.Range(0, 5).Select(i => i * 0.020).ToArray();

        var d = NmBurstDetector.DetectInEpisode(ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely);

        Assert.True(d.IsCandidate);
        Assert.True(d.IsIndeterminate);
        Assert.False(d.IsCompliant);
    }
}
