using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.5.3：Activity 段的唤醒观察窗按「是否观测到快发」在 1700/1500 之间选。
/// <para>
/// 但 §7.5.5 同时要求「无 profile 时 M1 行为不变」——M1 的老口径是
/// <c>RepeatMessageMs ?? CycleMs</c>。两者必须同时成立，因此
/// <see cref="NmEffectiveTiming"/> 需要携带「谁显式声明了 T_REPEAT_MESSAGE」这一事实：
/// 没人声明时不能凭空用 1700/1500 替换掉 M1 的 500ms 观察窗。
/// </para>
/// </summary>
public class NmActivityTimingTests
{
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static readonly NmMessageDef Def = new(
        "0x500", false, "NM", "A", 8, true, CannmReasons, 500, 2000, true);

    private static ReplayFrame F(double t)
        => new(t, 0x500, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None);

    /// <summary>只有周期被显式配置、T_REPEAT_MESSAGE 无人声明。</summary>
    private static NmEffectiveTiming Undeclared(int cycleMs) => new(
        CycleMs: cycleMs, TimeoutMs: 2000, WaitBusSleepMs: 2000,
        RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
        ImmediateCycleMs: 20, ImmediateCount: 10,
        StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1,
        RepeatWindowSource: NmRepeatWindowSource.None);

    /// <summary>
    /// 企标声明了双窗口（吉利 §5.5）。必须同时声明 <c>ImmediateCycleDeclared</c>：
    /// spec §7.5.3.1「回退时不得选 1700」——未声明快发周期时检测器是
    /// cycle_ratio_fallback，检出的候选不能用来选 1700 窗。
    /// </summary>
    private static NmEffectiveTiming Declared() => new(
        CycleMs: 500, TimeoutMs: 2000, WaitBusSleepMs: 2000,
        RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
        ImmediateCycleMs: 20, ImmediateCount: 10,
        StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1,
        RepeatWindowSource: NmRepeatWindowSource.StandardDual,
        ImmediateCycleDeclared: true);

    private static IReadOnlyList<NmSegment> Build(
        double[] times, NmEffectiveTiming timing, double windowEnd)
    {
        var frames = times.Select(t => new NmFrame(Def, F(t))).ToArray();
        var episodes = NmEpisodeBuilder.Build(
            frames, timing.TimeoutMs, windowStart: 0, windowEnd: windowEnd);
        return NmActivityBuilder.Build(
            frames, Def, episodes, timing, allNmFrames: frames, windowEnd: windowEnd);
    }

    [Fact]
    public void Build_NoRepeatWindowDeclared_KeepsM1FallbackToCycle()
    {
        // M1 老行为：RepeatMessageMs 为空时用周期（500ms）当观察窗。
        // 若这里变成 1500/1700，就是「无 profile 时行为变了」（spec §7.5.5 禁止）。
        var segments = Build([0.0, 0.5, 1.0, 1.5, 2.0], Undeclared(cycleMs: 500), 2.01);

        var wakeup = Assert.Single(segments, s => s.Kind == "wakeup");
        Assert.Equal(0.5, wakeup.End, precision: 6);
    }

    [Fact]
    public void Build_DeclaredWithoutBurst_Uses1500Window()
    {
        // 无快发 → 1500ms 窗口（吉利 §5.5「RMS 不含快发」）。
        var times = Enumerable.Range(0, 5).Select(i => i * 0.5).ToArray();
        var segments = Build(times, Declared(), 2.01);

        var wakeup = Assert.Single(segments, s => s.Kind == "wakeup");
        // 末帧 2.0 早于 0+1.5，窗口不会被夹到末帧。
        Assert.Equal(1.5, wakeup.End, precision: 6);
    }

    [Fact]
    public void Build_DeclaredWithBurst_Uses1700Window()
    {
        // spec §7.5.3 / §7.5 错误表：已声明 T_REPEAT_MESSAGE 时，快发只用来**选窗**，
        // 唤醒段是整个 RMS 的推断时长（1700），不是快发段末帧。
        // 「有快发就画到 burstEnd」正是 §7.5 错误表要纠正的 M1 毛病
        // （真实 trace 第二段剩下 2.14s 的 RMS 被画成「发送中」）。
        var times = new[] { 0.0, 0.02, 0.04, 0.06, 1.71 };
        var segments = Build(times, Declared(), 1.72);

        var wakeup = Assert.Single(segments, s => s.Kind == "wakeup");
        // 0 + 1700ms，不是 1500、不是 500、也不是快发末帧 0.06。
        Assert.Equal(1.7, wakeup.End, precision: 6);
    }

    [Fact]
    public void Build_Declared_BurstMidEpisodeStillSelects1700()
    {
        // spec §7.5.3 的判据是「episode 内**有**快发段」，不要求它从首帧起算。
        // 若误用 anchorAtStart，这里会错选 1500。
        var times = new[] { 0.0, 0.5, 1.0, 1.02, 1.04, 1.06, 1.08, 1.10, 2.0 };
        var segments = Build(times, Declared(), 2.01);

        var wakeup = Assert.Single(segments, s => s.Kind == "wakeup");
        Assert.Equal(1.7, wakeup.End, precision: 6);
    }

    [Fact]
    public void Build_WindowIsClampedToLastFrame()
    {
        // 末帧早于兜底窗：观察窗不得超出末帧，否则会画出一段没有帧的「唤醒」。
        var segments = Build([0.0, 0.5], Declared(), 0.51);

        var wakeup = Assert.Single(segments, s => s.Kind == "wakeup");
        Assert.Equal(0.5, wakeup.End, precision: 6);
    }

    [Fact]
    public void Build_DeclaredFallbackDetector_DoesNotSelect1700()
    {
        // spec §7.5.3.1：「回退时不得选 1700」。未声明 ImmediateCycleMs 时检测器是
        // cycle_ratio_fallback，它检出的候选证明不了符合 N_ImmediateNM_TIMES，
        // 必须按 1500 兜底。
        var noImmediateDecl = new NmEffectiveTiming(
            CycleMs: 500, TimeoutMs: 2000, WaitBusSleepMs: 2000,
            RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
            ImmediateCycleMs: 20, ImmediateCount: 10,
            StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1,
            RepeatWindowSource: NmRepeatWindowSource.StandardDual,
            ImmediateCycleDeclared: false);

        // 快发形态存在，但只有回退检测器能看见它。
        var times = new[] { 0.0, 0.02, 0.04, 0.06, 2.0 };
        var segments = Build(times, noImmediateDecl, 2.01);

        var wakeup = Assert.Single(segments, s => s.Kind == "wakeup");
        Assert.Equal(1.5, wakeup.End, precision: 6);
    }

    [Fact]
    public void Build_M1Path_LastFrameAtWindowEnd_IsWindowTruncated()
    {
        // spec §7.5.2 真值表第 1 行对 M1 路径同样适用：末帧恰好是窗口终点时，
        // 这段被窗口切断，时长只是下限。不置位会把「还在发、没测完」呈现成完整时长。
        var times = Enumerable.Range(0, 6).Select(i => i * 0.5).ToArray();
        // 窗口终点恰好是末帧 2.5。
        var segments = Build(times, Undeclared(cycleMs: 500), 2.5);

        var active = Assert.Single(segments, s => s.Kind == "active");
        Assert.True(active.IsWindowTruncated, "末帧 == windowEnd，active 段被切断");
    }

    [Fact]
    public void Build_M1Path_LastFrameBeforeWindowEnd_NotTruncated()
    {
        // 对照组：末帧 < windowEnd 时不是截断。
        var times = Enumerable.Range(0, 6).Select(i => i * 0.5).ToArray();
        var segments = Build(times, Undeclared(cycleMs: 500), 5.0);

        var active = Assert.Single(segments, s => s.Kind == "active");
        Assert.False(active.IsWindowTruncated);
    }
}
