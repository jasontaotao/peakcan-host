using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 回归：同一 NM 报文出现两段唤醒（段间静默 ≥ timeout）时，报文级活动段必须
/// 按 episode 拆分，不能跨静默空档画成一条连续段。
/// </summary>
public class NmMultiEpisodeActivityTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static ReplayFrame F(double t, uint id = 0x500)
        => new(t, id, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None);

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[]
        {
            new NmMessageDef("0x500", false, "NM", "A", 8, true,
                CannmReasons, 500, 2000, true),
        },
        new NmTimings(CycleMs: 500, TimeoutMs: 2000, RepeatMessageMs: 1600),
        new[] { new NmNodeDef("A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance());

    [Fact]
    public void Activity_DoesNotSpanSilenceBetweenEpisodes()
    {
        // 0.0–1.0s 第一段，3.0–4.0s 第二段；段间 2.0s 静默恰好等于 timeout，切出两个 episode。
        var frames = new[]
        {
            F(0.0), F(0.5), F(1.0),
            F(3.0), F(3.5), F(4.0),
        };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("src", null, frames, null, Profile(), 0, 4.01));

        Assert.Equal(2, report.Episodes.Count);
        var message = Assert.Single(report.Messages);

        // 活动/释放段不得与两段之间的静默空档 [1.0, 3.0] 有重叠；
        // 该空档必须由 silent 段覆盖，而不是被并进"发送中"。
        Assert.DoesNotContain(message.Activity,
            s => s.Kind != "silent" && s.Start < 3.0 - 1e-6 && s.End > 1.0 + 1e-6);
        Assert.Contains(message.Activity,
            s => s.Kind == "silent" && Math.Abs(s.Start - 1.0) < 1e-6 && Math.Abs(s.End - 3.0) < 1e-6);

        // 通用不变量：段非零长且两两不重叠（时间条上不会叠画）。
        Assert.All(message.Activity, s => Assert.True(s.End > s.Start));
        var sorted = message.Activity.OrderBy(s => s.Start).ToArray();
        for (int i = 1; i < sorted.Length; i++)
            Assert.True(sorted[i].Start >= sorted[i - 1].End - 1e-6);
    }

    [Fact]
    public void Activity_HasWakeupPhaseInEachEpisode()
    {
        var frames = new[]
        {
            F(0.0), F(0.1), F(0.2), F(0.7), F(1.2),
            F(3.4), F(3.5), F(3.6), F(4.1), F(4.6),
        };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("src", null, frames, null, Profile(), 0, 4.61));

        Assert.Equal(2, report.Episodes.Count);
        var message = Assert.Single(report.Messages);

        // 每个 episode 都应有自己的唤醒段，且第二段必须从 episode 首帧（3.4）开始，
        // 不能把 Start 闭区间上的帧漏掉。
        Assert.Equal(2, message.Activity.Count(s => s.Kind == "wakeup"));
        Assert.Contains(message.Activity,
            s => s.Kind == "wakeup" && Math.Abs(s.Start - 3.4) < 1e-6);
    }

    [Fact]
    public void Activity_TailIsClampedToNextEpisodeStart_EvenWhenMessageTimeoutIsLonger()
    {
        // episode 用全局 2000ms 切分，但报文自身 timeout 3000ms：
        // 若不夹住尾段，ep0 的 silent 会外推到 4.0s，压到 ep1 的唤醒段上。
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true,
            CannmReasons, 500, 3000, true);
        var frames = new[] { F(0.0), F(0.5), F(1.0), F(3.5), F(4.0), F(4.5) }
            .Select(f => new NmFrame(def, f))
            .ToArray();
        var episodes = NmEpisodeBuilder.Build(frames, timeoutMs: 2000, windowStart: 0, windowEnd: 4.61);

        var segments = NmActivityBuilder.Build(
            frames, def, episodes,
            effectiveCycleMs: 500, repeatMessageMs: 1600,
            allNmFrames: frames, windowEnd: 4.61, effectiveTimeoutMs: 3000);

        var sorted = segments.OrderBy(s => s.Start).ToArray();
        for (int i = 1; i < sorted.Length; i++)
            Assert.True(sorted[i].Start >= sorted[i - 1].End - 1e-6,
                $"段重叠：{sorted[i - 1]} 与 {sorted[i]}");
    }

    [Fact]
    public void Activity_MessageAbsentInMiddleEpisode_ProducesNoSegmentThere()
    {
        // A 只出现在第 1、3 个 episode，B 出现在第 2 个。
        var aDef = new NmMessageDef("0x500", false, "NM_A", "A", 8, true,
            CannmReasons, 500, 2000, true);
        var bDef = new NmMessageDef("0x501", false, "NM_B", "B", 8, true,
            CannmReasons, 500, 2000, true);
        var aFrames = new[] { F(0.0, 0x500), F(0.5, 0x500), F(10.0, 0x500), F(10.5, 0x500) }
            .Select(f => new NmFrame(aDef, f)).ToArray();
        var bFrames = new[] { F(5.0, 0x501), F(5.5, 0x501) }
            .Select(f => new NmFrame(bDef, f)).ToArray();
        var all = new[] { aFrames[0], aFrames[1], aFrames[2], aFrames[3], bFrames[0], bFrames[1] };
        var episodes = NmEpisodeBuilder.Build(all, timeoutMs: 2000, windowStart: 0, windowEnd: 11);

        var segments = NmActivityBuilder.Build(
            aFrames, aDef, episodes,
            effectiveCycleMs: 500, repeatMessageMs: 1600,
            allNmFrames: all, windowEnd: 11, effectiveTimeoutMs: 2000);

        // 中间 episode（5.0–5.5）不属于 A：不得生成任何从该区间起始的段；
        // 第 3 个 episode（10.0–10.5）仍应正常生成 A 的段。
        Assert.Equal(3, episodes.Count);
        Assert.DoesNotContain(segments,
            s => s.Start >= 5.0 - 1e-6 && s.Start <= 5.5 + 1e-6);
        Assert.Contains(segments, s => s.Start >= 10.0 - 1e-6);
    }
}
