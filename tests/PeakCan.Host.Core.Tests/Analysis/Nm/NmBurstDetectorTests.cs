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

    // ---------------- anchorAtStart（spec §7.3 唤醒终点语义） ----------------

    [Fact]
    public void DetectInEpisode_AnchorAtStart_MidEpisodeBurstIsNotWakeupEnd()
    {
        // 报文先按周期发几帧、中途才出现快发。唤醒终点必须仍是首帧起的段，
        // 不能跳到中途的快发上——否则时间条会画出「先正常发送、后唤醒」。
        var ts = new[] { 0.0, 0.5, 1.0, 1.02, 1.04, 1.06, 1.08, 2.0 };

        var anchored = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely, anchorAtStart: true);
        var free = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely, anchorAtStart: false);

        // 锚定模式：快发不在首帧起算 → 判为无候选。
        Assert.False(anchored.IsCandidate);
        // 自由模式：能检出中途那段快发（规则求值需要它）。
        Assert.True(free.IsCandidate);
        Assert.Equal(1.0, free.Start, precision: 6);
    }

    [Fact]
    public void DetectInEpisode_AnchorAtStart_ShorterFirstRunLosesToLongerMidRun()
    {
        // 关键区分场景：**首帧起就有一小段快发**，中途还有一段更长的快发。
        // 自由模式会选「最长的那段」（中途），锚定模式必须只认首帧起的那段。
        // 上一条测试的首个间隔 0.5s 已超阈值，runStart 从未在 0 处启动，
        // 所以它区分不出 anchorAtStart 的实现差异——这条才能。
        var ts = new[]
        {
            0.0, 0.02, 0.04,            // 首帧起 2 个短间隔
            0.5,                        // 长间隔断开
            1.0, 1.02, 1.04, 1.06, 1.08, 1.10,  // 中途 5 个短间隔（更长）
        };

        var anchored = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely, anchorAtStart: true);
        var free = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely, anchorAtStart: false);

        // 锚定：只认首帧起的那段（0.0–0.04），不能被中途更长的段取代。
        Assert.True(anchored.IsCandidate);
        Assert.Equal(0.0, anchored.Start, precision: 6);
        Assert.Equal(0.04, anchored.End, precision: 6);

        // 自由：选最长的那段（中途，1.0 起）。
        Assert.True(free.IsCandidate);
        Assert.Equal(1.0, free.Start, precision: 6);
        Assert.True(free.IntervalCount > anchored.IntervalCount);
    }

    [Fact]
    public void DetectInEpisode_AnchorAtStart_FirstFrameBurstIsDetected()
    {
        // 快发确实从首帧起算时，锚定模式必须检出。
        var ts = new[] { 0.0, 0.02, 0.04, 0.06, 0.5 };

        var d = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely, anchorAtStart: true);

        Assert.True(d.IsCandidate);
        Assert.Equal(0.0, d.Start, precision: 6);
        Assert.Equal(0.06, d.End, precision: 6);
    }

    [Fact]
    public void DetectInEpisode_PreferImmediateCycleFalse_UsesCycleRatioThreshold()
    {
        // spec §7.5.3.1 回退表：禁用 immediate 路径时用 0.5×CycleMs = 250ms。
        // 100ms 间隔在 immediate 路径下不算快发（>40ms），在回退路径下算（<250ms）。
        var ts = new[] { 0.0, 0.1, 0.2, 0.3, 1.0 };

        var immediate = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely,
            anchorAtStart: true, preferImmediateCycle: true);
        var fallback = NmBurstDetector.DetectInEpisode(
            ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely,
            anchorAtStart: true, preferImmediateCycle: false);

        Assert.False(immediate.IsCandidate);
        Assert.True(fallback.IsCandidate);
        Assert.Equal("cycle_ratio_fallback", fallback.Detector);
    }
}
