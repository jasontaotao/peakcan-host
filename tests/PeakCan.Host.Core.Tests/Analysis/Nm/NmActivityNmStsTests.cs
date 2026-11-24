using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.5.1：Activity 段必须按 NMSts 切，不再只靠时间推断。
/// <para>
/// 验收对照 §7.5.5 与 §14.3 的真实 trace 形态：
/// 第一段 Repeat 0.094675–2.215708 后接 Normal；第二段 Repeat 12.677179–15.002182。
/// </para>
/// </summary>
public class NmActivityNmStsTests
{
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static readonly NmMessageDef Def = new(
        "0x500", false, "NM", "A", 8, true, CannmReasons, 500, 2000, true);

    /// <summary>NMSts 声明在 byte3.bit0（吉利 §5.4.4）。</summary>
    private static readonly NmBitRole[] Roles = [new NmBitRole(3, 0, "NmSts")];

    private static ReplayFrame F(double t, bool nmsts)
        => new(t, 0x500, 8, new byte[] { 0xF4, 0, 0, (byte)(nmsts ? 1 : 0), 0, 0, 0, 0 },
            FrameFlags.None);

    private static readonly NmEffectiveTiming Timing = new(
        CycleMs: 500, TimeoutMs: 2000, WaitBusSleepMs: 2000,
        RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
        ImmediateCycleMs: 20, ImmediateCount: 10,
        StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1,
        RepeatWindowSource: NmRepeatWindowSource.StandardDual, ImmediateCycleDeclared: true);

    private static IReadOnlyList<NmSegment> Build(
        (double T, bool NmSts)[] points, NmBitRole[]? roles = null, double? windowEnd = null)
    {
        var frames = points.Select(p => new NmFrame(Def, F(p.T, p.NmSts))).ToArray();
        var end = windowEnd ?? points[^1].T + 0.01;
        var episodes = NmEpisodeBuilder.Build(frames, 2000, 0, end);
        var probe = NmStsResolver.TryResolve(roles ?? Roles, null, Def.CanIdHex, false, out _);
        return NmActivityBuilder.Build(
            frames, Def, episodes, Timing, allNmFrames: frames, windowEnd: end,
            stsProbe: probe);
    }

    [Fact]
    public void Build_WithNmSts_SplitsRepeatThenNormal()
    {
        // 0.0–1.5 NMSts=0（Repeat），1.5–3.0 NMSts=1（Normal）。
        var points = new (double, bool)[]
        {
            (0.0, false), (0.5, false), (1.0, false), (1.5, true), (2.0, true), (2.5, true),
        };

        var segments = Build(points);

        // 有状态位时不得再画 wakeup（spec §7.5.1 第 4 条：避免「wakeup + repeat」叠两段）。
        Assert.DoesNotContain(segments, s => s.Kind == "wakeup");
        var repeat = Assert.Single(segments, s => s.Kind == "repeat");
        Assert.Equal(0.0, repeat.Start, precision: 6);
        Assert.Equal(1.5, repeat.End, precision: 6);
        Assert.Equal("measured", repeat.Evidence);

        var normal = Assert.Single(segments, s => s.Kind == "normal");
        Assert.Equal(1.5, normal.Start, precision: 6);
        Assert.Equal(2.5, normal.End, precision: 6);
        Assert.Equal("measured", normal.Evidence);
    }

    [Fact]
    public void Build_WithNmSts_RepeatKeepsBurstSubsegments()
    {
        // spec §7.5.1.1：repeat 段内必须保留快发子状态，否则 NM_03/NM_04 的证据被抹掉。
        // 10 帧 × 20ms 快发 → immediate 子段；之后 500ms 稳态 → normal_transmit 子段。
        var points = new List<(double, bool)> { (0.0, false) };
        for (var i = 1; i < 10; i++) points.Add((i * 0.02, false));
        points.Add((0.5, false));
        points.Add((1.0, false));
        points.Add((1.5, false));

        var segments = Build(points.ToArray());

        var immediate = Assert.Single(segments, s => s.Kind == "immediate");
        Assert.Equal(0.0, immediate.Start, precision: 6);
        Assert.Equal("measured", immediate.Evidence);

        // 快发之后仍是 NMSts=0，归 repeat 而不是 normal。
        // （§7.5.1.1 管这部分叫 normal_transmit，§7.5.5 验收表叫 repeat；
        //   两者同义同区间，取验收表的 repeat，见 AppendRun 的裁决注释。）
        Assert.Contains(segments, s => s.Kind == "repeat");
        Assert.DoesNotContain(segments, s => s.Kind == "normal");
        Assert.DoesNotContain(segments, s => s.Kind == "normal_transmit");
    }

    [Fact]
    public void Build_SingleStateInWindow_StaysMeasuredAndNotTruncated()
    {
        // spec §7.5.1 退回条件第 3 行 + §7.5.2 真值表第 4 行：窗口内只有单一状态、
        // **终点是末帧**（末帧 < windowEnd）时仍按该值画单段，Evidence=measured、
        // StateBoundaryKnown=false、IsWindowTruncated=false，不得退回 wakeup。
        // 这条是最容易做错的一条：退回推断会把只覆盖 NOS 的窗口重新画成 wakeup。
        var points = new (double, bool)[] { (0.0, true), (0.5, true), (1.0, true) };

        var segments = Build(points, windowEnd: 1.01);

        Assert.DoesNotContain(segments, s => s.Kind == "wakeup");
        var normal = Assert.Single(segments, s => s.Kind == "normal");
        Assert.Equal("measured", normal.Evidence);
        Assert.False(normal.StateBoundaryKnown, "窗口内没有状态跃迁，边界不可判明");
        Assert.False(normal.IsWindowTruncated, "末帧 1.0 < windowEnd 1.01，不是窗口切断");
    }

    [Fact]
    public void Build_WindowEndsExactlyOnLastFrame_IsWindowTruncated()
    {
        // spec §7.5.5 验收项：窗口**只覆盖**单一状态（末帧 == windowEnd）时必须
        // 仍为 measured，且 IsWindowTruncated=true——时长只是下限，用户要知道段被切了。
        // 与上一条的区别只有末帧是否等于 windowEnd，这正是 §7.5.2 真值表第 1 行与第 4 行的分界。
        var points = new (double, bool)[] { (0.0, true), (0.5, true), (1.0, true) };

        var segments = Build(points, windowEnd: 1.0);

        Assert.DoesNotContain(segments, s => s.Kind == "wakeup");
        var normal = Assert.Single(segments, s => s.Kind == "normal");
        Assert.Equal("measured", normal.Evidence);
        Assert.True(normal.IsWindowTruncated, "末帧 == windowEnd，段被窗口切断");
        Assert.False(normal.StateBoundaryKnown, "被切断时状态边界不可判明");
    }

    [Fact]
    public void Build_RepeatEntirelyBurst_KeepsImmediateSegment()
    {
        // spec §7.5.1.1：RMS 由快发构成、后面没有稳态帧时，immediate 不能被抹掉——
        // 那正是最需要它作证的情形（NM_03/NM_04 的直接证据）。
        var points = new List<(double, bool)> { (0.0, false) };
        for (var i = 1; i < 10; i++) points.Add((i * 0.02, false));

        var segments = Build(points.ToArray(), windowEnd: 0.18);

        var immediate = Assert.Single(segments, s => s.Kind == "immediate");
        Assert.Equal(0.0, immediate.Start, precision: 6);
        Assert.Equal(0.18, immediate.End, precision: 6);
        Assert.Equal("measured", immediate.Evidence);
    }

    [Fact]
    public void Build_MessageStopsBeforeWindowEnd_NotTruncatedButBoundaryUnknown()
    {
        // spec §7.5.2：两个 bool **正交**。
        // 末帧 1.0 < windowEnd 5.0 → 不是窗口切断（IsWindowTruncated=false）；
        // 但窗口内只有单一状态、没有跃迁 → 状态边界不可判明（StateBoundaryKnown=false）。
        // 「报文停发」界定的是 episode 尾段，不是 NMSts 段的终点。
        var points = new (double, bool)[] { (0.0, true), (0.5, true), (1.0, true) };

        var segments = Build(points, windowEnd: 5.0);

        var normal = Assert.Single(segments, s => s.Kind == "normal");
        Assert.False(normal.IsWindowTruncated);
        Assert.False(normal.StateBoundaryKnown);
    }

    [Fact]
    public void Build_NoNmStsDeclared_FallsBackToTimeInference()
    {
        // spec §7.5.1 退回条件第 1 行：未声明 NMSts → 退回时间推断，Evidence=inferred。
        var points = new (double, bool)[] { (0.0, false), (0.5, false), (1.0, false) };

        var segments = Build(points, roles: []);

        Assert.Contains(segments, s => s.Kind == "wakeup");
        Assert.DoesNotContain(segments, s => s.Kind is "repeat" or "normal");
        Assert.All(segments, s => Assert.Equal("inferred", s.Evidence));
    }

    [Fact]
    public void Build_CrossEpisode_DoesNotMergeNmStsSegments()
    {
        // spec §7.5.1 第 3 条：跨 episode 不合并。两段各自的 repeat 必须是两段。
        var points = new (double, bool)[]
        {
            (0.0, false), (0.5, false),
            (5.0, false), (5.5, false),
        };

        var segments = Build(points, windowEnd: 5.51);

        var repeats = segments.Where(s => s.Kind == "repeat").ToArray();
        Assert.Equal(2, repeats.Length);
        Assert.All(repeats, s => Assert.True(s.End - s.Start <= 1.0));
    }

    [Fact]
    public void Build_UnreadableFrame_SplitsIntoUnknownNotWholeSegment()
    {
        // spec §7.5.1.2「逐帧不可读」：部分帧 DLC 不足 → 该帧区间标 unknown（inferred），
        // 其余实测帧仍为 measured。不允许因少数帧不可读就把整段降级。
        var frames = new[]
        {
            new NmFrame(Def, F(0.0, nmsts: false)),
            // DLC 只有 2 字节，读不到 byte3。
            new NmFrame(Def, new ReplayFrame(0.5, 0x500, 2, new byte[] { 0xF4, 0 },
                FrameFlags.None)),
            new NmFrame(Def, F(1.0, nmsts: false)),
            new NmFrame(Def, F(1.5, nmsts: false)),
        };
        var episodes = NmEpisodeBuilder.Build(frames, 2000, 0, 1.51);
        var probe = NmStsResolver.TryResolve(Roles, null, Def.CanIdHex, false, out _);

        var segments = NmActivityBuilder.Build(
            frames, Def, episodes, Timing, allNmFrames: frames, windowEnd: 1.51,
            stsProbe: probe);

        // 整段不能被降级成 inferred。
        Assert.Contains(segments, s => s.Evidence == "measured");
        Assert.Contains(segments, s => s.Evidence == "inferred");
    }

    [Fact]
    public void Build_AllFramesUnreadable_FallsBackToInference()
    {
        // 每一帧都读不出（DLC 全为 2）：没有可用的状态位，必须整体退回推断。
        var frames = new[]
        {
            new NmFrame(Def, new ReplayFrame(0.0, 0x500, 2, new byte[] { 0xF4, 0 }, FrameFlags.None)),
            new NmFrame(Def, new ReplayFrame(0.5, 0x500, 2, new byte[] { 0xF4, 0 }, FrameFlags.None)),
            new NmFrame(Def, new ReplayFrame(1.0, 0x500, 2, new byte[] { 0xF4, 0 }, FrameFlags.None)),
        };
        var episodes = NmEpisodeBuilder.Build(frames, 2000, 0, 1.01);
        var probe = NmStsResolver.TryResolve(Roles, null, Def.CanIdHex, false, out _);

        var segments = NmActivityBuilder.Build(
            frames, Def, episodes, Timing, allNmFrames: frames, windowEnd: 1.01,
            stsProbe: probe);

        Assert.Contains(segments, s => s.Kind == "wakeup");
        Assert.DoesNotContain(segments, s => s.Kind is "repeat" or "normal");
    }
}
