using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec 收紧修复（2026-11-20）的回归用例：§7.9 unknowns、§7.5.6 missed_frame、
/// §7.5.1/§7.5.5 窗口首尾静默、§7.3 阶段与 indeterminate_end、§6.6.2 用户超时同口径。
/// </summary>
public class NmSpecClosureTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];
    private static readonly string[] ReleasedKinds = ["wakeup", "active", "released"];
    private static readonly byte[] NodePayload = [0xF4, 0, 0, 0, 0, 0, 0, 0];

    private static ReplayFrame F(double t, uint id = 0x500, bool ext = false, params byte[] data)
        => new(t, id, (byte)data.Length, data, FrameFlags.None, ext);

    private static NmMessageDef Def(
        string id = "0x500", int? cycle = 500, int? timeout = 2000,
        string name = "NM", string sender = "A", byte dlc = 8, bool ext = false)
        => new(id, ext, name, sender, dlc, true, CannmReasons, cycle, timeout, true);

    private static NmProfile Profile(params NmMessageDef[] messages)
        => Profile(new NmTimings(500, 2000), messages);

    private static NmProfile Profile(NmTimings timings, params NmMessageDef[] messages) => new(
        NmProfileSource.Dbc,
        messages,
        timings,
        new[] { new NmNodeDef("A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance());

    private static NmReport Run(IReadOnlyList<ReplayFrame> frames, NmProfile profile, double end)
        => NmAnalyzer.Analyze(new NmAnalysisRequest("src", null, frames, null, profile, 0, end));

    // ---------------- §7.9 六条 unknowns ----------------

    [Fact]
    public void Analyze_Unknowns_ContainSixEntriesIncludingDuplicateFrameAttribution()
    {
        // 无 NM 帧时没有 episode/空档附加项，可直接断言基线的六条。
        var report = Run(Array.Empty<ReplayFrame>(), Profile(Def()), 1.0);

        Assert.Equal(6, report.Unknowns.Count);
        Assert.Contains(report.Unknowns, u => u.Contains("duplicate_frame_attribution"));
    }

    // ---------------- §7.5.6 missed_frame ----------------

    [Fact]
    public void Analyze_MissedFrameInterval_ProducesInferredEvent()
    {
        // 500ms 周期：1.5×cycle = 750ms；1.0s 间隔落在 (750ms, 2000ms] 内。
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload),
            F(1.0, data: NodePayload), F(2.0, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 2.01);

        var missed = Assert.Single(report.Events, e => e.Type == "missed_frame");
        Assert.Equal(1.0, missed.T);
        Assert.Equal("inferred", missed.EvidenceKind);
        Assert.Equal("0x500", missed.CanIdHex);
        Assert.Contains("1.00s", missed.Detail);
        Assert.Contains("可能漏发 1 帧", missed.Detail);
    }

    [Fact]
    public void Analyze_MissedFrameAtStrictLowerBoundary_NotProduced()
    {
        // gap 恰好等于 1.5×cycle：判据是严格大于，不产事件。
        var frames = new[] { F(0, data: NodePayload), F(0.75, data: NodePayload) };

        var report = Run(frames, Profile(Def()), 0.75);

        Assert.DoesNotContain(report.Events, e => e.Type == "missed_frame");
    }

    [Fact]
    public void Analyze_MissedFrameAtTimeoutBoundary_IsProduced()
    {
        // gap 恰好等于超时（2000ms）：判据是 ≤ timeout，仍算漏发。
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload),
            F(2.5, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 2.51);

        Assert.Contains(report.Events, e => e.Type == "missed_frame" && e.T == 0.5);
    }

    [Fact]
    public void Analyze_MissedFrameBeyondTimeout_NotProduced()
    {
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload),
            F(2.51, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 2.6);

        Assert.DoesNotContain(report.Events, e => e.Type == "missed_frame");
    }

    [Fact]
    public void Analyze_SharedId_MissedFrameStillProduced()
    {
        // 共享 ID 只抑制周期偏差结论，不影响漏帧证据。
        var profile = Profile(Def()) with { Layout = new NmLayout(NidByte: 0) };
        var frames = new[]
        {
            F(0, data: NodePayload),
            F(0.5, data: new byte[] { 0x27, 0, 0, 0, 0, 0, 0, 0 }),
            F(1.0, data: NodePayload),
            F(2.0, data: NodePayload),
        };

        var report = Run(frames, profile, 2.01);

        Assert.Contains(report.Events, e => e.Type == "missed_frame" && e.T == 1.0);
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");
    }

    [Fact]
    public void Analyze_MissedFrameEvents_RespectPerMessageBudgetAndRecordTruncation()
    {
        // 202 帧、每帧间隔 1.0s → 201 个漏发间隔，超过单报文 200 条事件预算。
        var frames = Enumerable.Range(0, 202)
            .Select(i => F(i * 1.0, data: NodePayload))
            .ToArray();

        var report = Run(frames, Profile(Def()), 201.5);

        Assert.Equal(200, report.Events.Count(e => e.Type == "missed_frame"));
        var truncation = Assert.Single(
            report.Truncations, t => t.Section == "events:0x500");
        Assert.Equal(200, truncation.Returned);
        Assert.Equal(201, truncation.Total);
    }

    [Fact]
    public void Analyze_MissedFrame_StdAndExtendedSameNumericId_AreCountedSeparately()
    {
        // 标准 0x500 与扩展 0x500 是两条报文：合并分组会把 1.0s 间隔拆成 0.5s，
        // 漏帧证据被掩盖；必须分组建索引。
        var profile = Profile(new NmTimings(500, 2000), Def(), Def(name: "NM_Ext", ext: true));
        var frames = new[]
        {
            F(0.0, 0x500, false, NodePayload),
            F(0.5, 0x500, true, NodePayload),
            F(1.0, 0x500, false, NodePayload),
            F(1.5, 0x500, true, NodePayload),
        };

        var report = Run(frames, profile, 1.51);

        var missed = report.Events.Where(e => e.Type == "missed_frame").ToArray();
        Assert.Equal(2, missed.Length);
        Assert.Contains(missed, e => e.T == 0.0);
        Assert.Contains(missed, e => e.T == 0.5);
    }

    [Fact]
    public void Analyze_MissedFrameLongGap_ReportsEstimatedFrameCount()
    {
        // 1.9s 间隔 / 500ms 周期 ≈ 漏发 3 帧，不能一律写成"1 帧"。
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload), F(2.4, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 2.41);

        var missed = Assert.Single(report.Events, e => e.Type == "missed_frame");
        Assert.Contains("1.90s", missed.Detail);
        Assert.Contains("约 3 帧", missed.Detail);
    }

    // ---------------- §7.5.1 / §7.5.5 窗口首尾静默 ----------------

    [Fact]
    public void Analyze_TailSilenceAfterLastNmFrame_ProducesAllNmSilent()
    {
        // 窗口 0–5s，NM 在 1.0s 停止；尾段静默 4s ≥ 2s 超时。
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload), F(1.0, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 5.0);

        var finding = Assert.Single(report.Findings, f => f.Code == "NM_ALL_NM_SILENT");
        Assert.Equal("info", finding.Severity);
        Assert.Equal(1.0, finding.EvidenceTimes[0]);
        Assert.Equal(5.0, finding.EvidenceTimes[1]);
        Assert.Contains(report.Events, e => e.Type == "all_nm_silent" && e.T == 1.0);
    }

    [Fact]
    public void Analyze_TailSilenceBelowTimeout_NotProduced()
    {
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload), F(1.0, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 2.5);

        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_ALL_NM_SILENT");
    }

    [Fact]
    public void Analyze_HeadSilence_ProducesTraceBoundaryNotFault()
    {
        // 窗口从 0 开始，首帧在 5.0s：开头 5s 静默 ≥ 2s，属记录边界。
        var frames = new[]
        {
            F(5.0, data: NodePayload), F(5.5, data: NodePayload), F(6.0, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 7.5);

        Assert.Contains(report.Findings,
            f => f.Code == "NM_TRACE_BOUNDARY" && f.Message.Contains("窗口开头"));
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_NODE_DROPOUT");
    }

    [Fact]
    public void Analyze_InternalEpisodeGap_DoesNotDuplicateAllNmSilent()
    {
        // 两个 episode 之间的 3s 静默只由 §7.5.1 报一次，尾部静默不足阈值不再叠加。
        var frames = new[]
        {
            F(0, data: NodePayload), F(0.5, data: NodePayload), F(1.0, data: NodePayload),
            F(4.0, data: NodePayload), F(4.5, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 5.0);

        Assert.Single(report.Findings, f => f.Code == "NM_ALL_NM_SILENT");
    }

    // ---------------- §6.6.2 / §5.1 单报文超时同口径 ----------------

    [Fact]
    public void Analyze_UserTimeoutOverride_DrivesGapAndCycleConsistently()
    {
        // 用户在补全里把超时改成 1000ms、周期 400ms：1.2s 间隔是单报文静默，
        // 不是全网静默；周期统计也必须按同一 1000ms 剔除该间隔。
        var baseProfile = Profile(
            new NmTimings(500, 2000),
            Def(cycle: 500, timeout: 2000),
            Def(id: "0x501", cycle: 500, timeout: 2000, name: "NM_B", sender: "B"));
        var profile = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(
            Timings: new NmTimings(CycleMs: 400, TimeoutMs: 1000)));

        var frames = new List<ReplayFrame>();
        foreach (var t in new[] { 0.0, 0.4, 0.8, 1.2, 1.6, 2.8, 3.2 })
            frames.Add(F(t, 0x500, false, NodePayload));
        for (int i = 0; i <= 8; i++)
            frames.Add(F(i * 0.4, 0x501, false, NodePayload));

        var report = Run(frames, profile, 3.21);

        // 用户值已逐报文落盘到 profile。
        Assert.All(profile.Messages, m => Assert.Equal(1000, m.TimeoutMs));
        Assert.All(profile.Messages, m => Assert.Equal(400, m.CycleMs));

        var silent = Assert.Single(
            report.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
        Assert.Contains("1000ms", silent.Message);
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_ALL_NM_SILENT");

        var message = Assert.Single(report.Messages, m => m.CanIdHex == "0x500");
        Assert.Equal(400, message.Cycle.MedianMs);
        Assert.Equal(5, message.Cycle.IntervalCount);
    }

    // ---------------- §7.3 阶段模型 ----------------

    [Fact]
    public void Analyze_Activity_WakeupAndActiveWithoutObservedBurst()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => F(i * 0.5, data: NodePayload))
            .ToArray();

        var report = Run(frames, Profile(new NmTimings(500, 2000, 1600), Def()), 2.6);

        var message = Assert.Single(report.Messages);
        Assert.Equal(2, message.Activity.Count);
        Assert.Equal("wakeup", message.Activity[0].Kind);
        Assert.Equal(0.0, message.Activity[0].Start);
        Assert.Equal(1.6, message.Activity[0].End);
        Assert.Equal("active", message.Activity[1].Kind);
        Assert.Equal(1.6, message.Activity[1].Start);
        Assert.Equal(2.5, message.Activity[1].End);
    }

    [Fact]
    public void Analyze_Activity_NoRepeatMessageFallsBackToCycle()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => F(i * 0.5, data: NodePayload))
            .ToArray();

        var report = Run(frames, Profile(new NmTimings(500, 2000), Def()), 2.5);

        var message = Assert.Single(report.Messages);
        Assert.Equal("wakeup", message.Activity[0].Kind);
        Assert.Equal(0.5, message.Activity[0].End);
    }

    [Fact]
    public void Analyze_Activity_ObservedBurstEndsWakeup()
    {
        var frames = new[]
        {
            F(0.0, data: NodePayload), F(0.05, data: NodePayload), F(0.1, data: NodePayload),
            F(0.6, data: NodePayload), F(1.1, data: NodePayload),
        };

        var report = Run(frames, Profile(new NmTimings(500, 2000, 1600), Def()), 1.11);

        var message = Assert.Single(report.Messages);
        Assert.Equal("wakeup", message.Activity[0].Kind);
        Assert.Equal(0.1, message.Activity[0].End);
        Assert.Equal("active", message.Activity[1].Kind);
        Assert.Equal(0.1, message.Activity[1].Start);
        Assert.Equal(1.1, message.Activity[1].End);
    }

    [Fact]
    public void Analyze_Activity_ReleasedThenSilentAcrossTrailingPhases()
    {
        var profile = Profile(
            new NmTimings(500, 2000),
            Def(),
            Def(id: "0x501", name: "NM_B", sender: "B"));

        var frames = new List<ReplayFrame>();
        foreach (var t in new[] { 0.0, 0.5, 1.0 }) frames.Add(F(t, 0x500, false, NodePayload));
        for (int i = 0; i <= 4; i++) frames.Add(F(i * 0.5, 0x501, false, NodePayload));

        var report = Run(frames, profile, 4.5);

        // A 先停、B 仍在发 → released；B 最后停、episode 自然结束 → silent 到静默阈值。
        var a = Assert.Single(report.Messages, m => m.CanIdHex == "0x500");
        Assert.Equal(ReleasedKinds, a.Activity.Select(s => s.Kind).ToArray());
        Assert.Equal(1.0, a.Activity[^1].Start);
        Assert.Equal(2.0, a.Activity[^1].End);

        var b = Assert.Single(report.Messages, m => m.CanIdHex == "0x501");
        Assert.Equal("silent", b.Activity[^1].Kind);
        Assert.Equal(2.0, b.Activity[^1].Start);
        Assert.Equal(4.0, b.Activity[^1].End);

        Assert.All(a.Activity, s => Assert.True(s.End > s.Start));
        Assert.All(b.Activity, s => Assert.True(s.End > s.Start));
    }

    [Fact]
    public void Analyze_Activity_SingleFrameHasNoZeroLengthSegment()
    {
        var frames = new[] { F(1.0, data: NodePayload) };

        var report = Run(frames, Profile(Def()), 2.5);

        var message = Assert.Single(report.Messages);
        Assert.Empty(message.Activity);
    }

    [Fact]
    public void Analyze_Activity_SilentAfterLastStoppingMessage()
    {
        // 单报文 episode 自然结束（窗口延续超过超时）：末帧后到达到静默阈值前是 silent。
        var frames = new[]
        {
            F(0.0, data: NodePayload), F(0.5, data: NodePayload), F(1.0, data: NodePayload),
        };

        var report = Run(frames, Profile(Def()), 5.0);

        var message = Assert.Single(report.Messages);
        var tail = message.Activity[^1];
        Assert.Equal("silent", tail.Kind);
        Assert.Equal(1.0, tail.Start);
        Assert.Equal(3.0, tail.End);
        Assert.All(message.Activity, s => Assert.True(s.End > s.Start));
    }

    [Fact]
    public void ActivityBuilder_ProducesSilentFromRealEpisodeBuilderOutput()
    {
        var nm = new[]
        {
            new NmFrame(Def(), F(0.0, data: NodePayload)),
            new NmFrame(Def(), F(0.5, data: NodePayload)),
            new NmFrame(Def(), F(1.0, data: NodePayload)),
        };
        // 用真实 episode 切分结果，不手搓 End 超出末帧的合成 episode。
        var episodes = NmEpisodeBuilder.Build(nm, timeoutMs: 2000, windowStart: 0, windowEnd: 5.0);

        // repeatMessageMs: null → 无声明 → 观察窗用周期（M1 老口径，spec §7.5.5）。
        var segments = NmActivityBuilder.Build(
            nm, Def(), episodes,
            new NmEffectiveTiming(500, 2000, 2000, 1700, 1500, 20, 10, 10, 120, 0.2),
            allNmFrames: nm, windowEnd: 5.0);

        Assert.Equal("silent", segments[^1].Kind);
        Assert.Equal(1.0, segments[^1].Start);
        Assert.Equal(3.0, segments[^1].End);
    }

    [Fact]
    public void Analyze_EpisodeBoundaryEnd_AddsIndeterminateEndUnknown()
    {
        // 窗口 1.0s 结束，最后一帧 0.5s：未满 2s 静默阈值 → BoundaryEnd。
        var frames = new[] { F(0, data: NodePayload), F(0.5, data: NodePayload) };

        var report = Run(frames, Profile(Def()), 1.0);

        Assert.True(Assert.Single(report.Episodes).BoundaryEnd);
        Assert.Contains(report.Unknowns, u => u.Contains("indeterminate_end"));
    }

    // ---------------- §6.2 DBC 属性越界告警 ----------------

    // NmAsrTimeoutTime=2000 越出 BA_DEF_ 声明的 0..1000。
    private const string NmRangeDbc = """
        VERSION ""

        BU_: ECU_A

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ S1 : 0|8@1+ (1,0) [0|255] "" ECU_A

        BA_DEF_ BO_ "MsgType" ENUM "CDD","CANNM";
        BA_ "MsgType" BO_ 1280 1;
        BA_DEF_ "NmAsrTimeoutTime" INT 0 1000;
        BA_ "NmAsrTimeoutTime" 2000;
        """;

    [Fact]
    public void Analyze_DbcNmAttributeOutOfRange_AddsRangeMismatchAssumption()
    {
        var dbc = DbcParser.Parse(NmRangeDbc).Value!;
        Assert.NotEmpty(dbc.NmAttributeWarnings);
        var profile = NmProfileBuilder.Build(dbc, null);

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Array.Empty<ReplayFrame>(), dbc, profile, 0, 1));

        Assert.Contains(report.Assumptions, a =>
            a.StartsWith("NM_ATTR_RANGE_MISMATCH: NmAsrTimeoutTime=", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DbcWarningWithoutPrefix_GetsCodePrefix()
    {
        // hil-core 若给出不带前缀的原始告警，Analyzer 负责补 "NM_ATTR_RANGE_MISMATCH: "。
        var parsed = DbcParser.Parse(NmRangeDbc).Value!;
        var dbc = parsed with { NmAttributeWarnings = new[] { "raw warning" } };
        var profile = NmProfileBuilder.Build(parsed, null);

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Array.Empty<ReplayFrame>(), dbc, profile, 0, 1));

        Assert.Contains(report.Assumptions, a => a == "NM_ATTR_RANGE_MISMATCH: raw warning");
    }

    [Fact]
    public void Analyze_DbcNmAttributeInRange_DoesNotAddRangeMismatchAssumption()
    {
        // 声明区间放宽到 0..3000 后 2000 合法：不产告警，也不写 assumptions。
        var dbc = DbcParser.Parse(NmRangeDbc.Replace("INT 0 1000", "INT 0 3000")).Value!;
        Assert.Empty(dbc.NmAttributeWarnings);
        var profile = NmProfileBuilder.Build(dbc, null);

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Array.Empty<ReplayFrame>(), dbc, profile, 0, 1));

        Assert.DoesNotContain(report.Assumptions, a => a.Contains("NM_ATTR_RANGE_MISMATCH"));
    }
}
