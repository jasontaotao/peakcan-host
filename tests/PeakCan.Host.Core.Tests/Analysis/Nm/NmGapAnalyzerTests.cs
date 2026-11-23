using System.Globalization;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmGapAnalyzerTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];
    private static readonly string[] SingleParticipant = ["0x500"];

    private static NmFrame Nm(double t, string id = "0x500", byte nid = 0xF4, bool ext = false)
        => new(new NmMessageDef(id, ext, "NM", "A", 8, true, TestReasons, 500, 2000),
               new ReplayFrame(t, CanId(id), 2, new byte[] { nid, 0 }, FrameFlags.None, ext));

    // CanIdHex 是 "0x..." 文本，解析时去掉 0x 前缀。
    private static uint CanId(string id)
        => uint.Parse(id.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static NmEpisode[] Episodes(params (double s, double e)[] spans)
        => spans.Select((s, i) => new NmEpisode(i, s.s, s.e, "active", null, SingleParticipant, false, false)).ToArray();

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, TestReasons, 500, 2000) },
        new NmTimings(500, 2000),
        new[] { new NmNodeDef("A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    private static NmSelectionDiagnostics Diag(bool regression = false)
        => new(0, 0, 0, 0, 0, 0, regression, false);

    // 企标 layout 的 NMSts 位（与负例集一致：byte3.bit0）。
    private const byte NmStsByte = 3;

    /// <summary>
    /// 带 NMSts 位的标准帧。readable=false 时 DLC 只到 byte1，
    /// 用来模拟「停发前最后一帧读不出 NMSts」。
    /// </summary>
    private static NmFrame NmWithSts(double t, byte nmsts, bool readable = true, string id = "0x500")
    {
        var payload = readable
            ? new byte[] { 0xF4, 0, 0, nmsts, 0, 0, 0, 0 }
            : new byte[] { 0xF4, 0 };
        return new(new NmMessageDef(id, false, "NM", "A", 8, true, TestReasons, 500, 2000),
                   new ReplayFrame(t, CanId(id), (byte)payload.Length, payload, FrameFlags.None));
    }

    /// <summary>M1 profile 上加/去 NMSts 位角色，专门控制 NMSts 是否可定位。</summary>
    private static NmProfile ProfileWithNmStsRole(bool includeNmSts)
        => Profile() with
        {
            StandardBitRoles = includeNmSts
                ? new[] { new NmBitRole(NmStsByte, 0, "NmSts") }
                : null,
        };

    /// <summary>另一条 NM 报文，0→4.5s 每 500ms 一帧，保证逐报文分支真的运行。</summary>
    private static NmFrame[] OtherMessageSteady(string id = "0x501")
        => Enumerable.Range(0, 10).Select(i => NmWithSts(i * 0.5, nmsts: 1, id: id)).ToArray();

    [Fact]
    public void Analyze_AllNmSilent_ProducesOnlyAllSilentFinding()
    {
        var nm = new[] { Nm(0), Nm(0.5), Nm(1.0), Nm(4.0) };
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 1.0), (4.0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_ALL_NM_SILENT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
        Assert.Contains(result.Unknowns, u => u.Contains("all_nm_silent_reason_unknown"));

        // 两个 episode 之间（1.0s → 4.0s）的静默只能由「间隙观测」站点产生：
        // 窗口尾部静默仅 1.0s（< 2s 超时）不会触发尾部站点。
        // spec §14.5：原因不可证明，观测必须标 inferred。
        var obs = Assert.Single(result.Observations, o => o.Kind == "all_nm_silent");
        Assert.True(obs.IsInference);
        Assert.Equal("inferred", obs.EvidenceKind);
    }

    [Fact]
    public void Analyze_BusWideSilence_ProducesBusWideFindingWithUnknown()
    {
        var nm = new[] { Nm(0), Nm(0.5), Nm(4.0) };
        var all = new[] { nm[0].Frame, nm[1].Frame, nm[2].Frame };
        var input = new NmGapInput("src", nm, all, Profile(),
            Episodes((0, 0.5), (4.0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_BUS_WIDE_SILENCE");
        Assert.Contains(result.Unknowns, u => u.Contains("recorder_gap_or_bus_sleep"));
        Assert.All(result.Findings, f => Assert.NotEmpty(f.EvidenceTimes));
    }

    [Fact]
    public void Analyze_PartialDropout_ProducesNodeDropout()
    {
        // 0x500 在 1.0s 后停止，0x501 一直发到 4.0s —— 这是"单报文掉线"，
        // 不是全网静默（全网静默时不会切出第二个 episode）。
        var nm = new List<NmFrame> { Nm(0, "0x500"), Nm(0.5, "0x500"), Nm(1.0, "0x500") };
        for (int i = 0; i <= 8; i++) nm.Add(Nm(i * 0.5, "0x501"));
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_ALL_NM_SILENT");
        Assert.Contains(result.Unknowns, u => u.Contains("dropout_receiver_side_or_node_internal_state_not_excluded"));
    }

    [Fact]
    public void Analyze_NodeEntersRssThenStops_NoDropoutFinding()
    {
        // NM_09：0x500 于 2.0s 进入 Ready Sleep State（NMSts=1）后停发，
        // 0x501 继续发到 4.5s。这是合规释放，不得归因为节点掉线。
        var nm = new List<NmFrame>
        {
            NmWithSts(0.0, 0), NmWithSts(0.5, 0), NmWithSts(1.0, 0), NmWithSts(2.0, 1),
        };
        nm.AddRange(OtherMessageSteady());
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(),
            ProfileWithNmStsRole(true), Episodes((0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        // 配套的 sender_silent 事件与「掉线原因未知」也必须一起消失。
        Assert.DoesNotContain(result.Events, e => e.Type == "sender_silent");
        Assert.DoesNotContain(result.Unknowns,
            u => u.Contains("dropout_receiver_side_or_node_internal_state_not_excluded"));
    }

    [Fact]
    public void Analyze_NodeStopsInRepeatMessageState_StillReportsDropout()
    {
        // 与上一个用例同形，但停发前最后一帧 NMSts=0（Repeat Message State）。
        // RMS 期间停发不是合规释放，必须继续报掉线——此用例钉住「不是无条件抑制」。
        var nm = new List<NmFrame>
        {
            NmWithSts(0.0, 0), NmWithSts(0.5, 0), NmWithSts(1.0, 0), NmWithSts(2.0, 0),
        };
        nm.AddRange(OtherMessageSteady());
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(),
            ProfileWithNmStsRole(true), Episodes((0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.Contains(result.Unknowns,
            u => u.Contains("dropout_receiver_side_or_node_internal_state_not_excluded"));
        // RepeatMessage 是**已知**状态：不得被记成「无法排除 RSS」的未知。
        // 这条与不可读/缺失角色用例互补，钉住四态不会被折叠成三态。
        Assert.DoesNotContain(result.Unknowns, u => u.Contains("dropout_cannot_exclude_rss"));
    }

    [Fact]
    public void Analyze_NoNmStsRole_StillReportsDropoutWithUnknown()
    {
        // profile 未声明 NmSts 位角色 → 无法区分合规停发与掉线：保守上报，
        // 并用 unknown 说明为什么无法排除 RSS。
        var nm = new List<NmFrame>
        {
            NmWithSts(0.0, 0), NmWithSts(0.5, 0), NmWithSts(1.0, 0), NmWithSts(2.0, 1),
        };
        nm.AddRange(OtherMessageSteady());
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(),
            ProfileWithNmStsRole(false), Episodes((0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.Contains(result.Unknowns,
            u => u.Contains("dropout_cannot_exclude_rss") && u.Contains("NmSts"));
    }

    [Fact]
    public void Analyze_UnreadableNmStsByte_StillReportsDropout()
    {
        // 停发前最后一帧 DLC 只有 2 字节，读不到 byte3 的 NMSts。
        // 「读不出」不等于 NMSts=1，不能据此抑制掉线。
        var nm = new List<NmFrame>
        {
            NmWithSts(0.0, 0), NmWithSts(0.5, 0), NmWithSts(1.0, 0),
            NmWithSts(2.0, 1, readable: false),
        };
        nm.AddRange(OtherMessageSteady());
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(),
            ProfileWithNmStsRole(true), Episodes((0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        // 「读不出」本身也必须作为未知上报：把 Unreadable 误当成 RepeatMessage
        // 会丢掉这条 unknown，却仍然产出 dropout，只断言 finding 无法发现这种折叠。
        Assert.Contains(result.Unknowns,
            u => u.Contains("dropout_cannot_exclude_rss") && u.Contains("读不出"));
    }

    [Fact]
    public void Analyze_SingleLongIntervalWhileOthersActive_ProducesSenderSilent()
    {
        // 0x500 中间断 3s 后又恢复：这是单个间隔超时，不是持续掉线。
        var nm = new List<NmFrame> { Nm(0, "0x500"), Nm(0.5, "0x500"), Nm(1.0, "0x500"),
                                     Nm(4.0, "0x500"), Nm(4.5, "0x500") };
        for (int i = 0; i <= 8; i++) nm.Add(Nm(i * 0.5, "0x501"));
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.Contains(result.Unknowns, u => u.Contains("sender_silent_receiver_side_not_excluded"));
    }

    [Fact]
    public void Analyze_TimestampRegression_AddsLogGapSuspected()
    {
        var nm = new[] { Nm(0), Nm(0.5) };
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 0.5)), new NmWindow(0, 1, "full"), Diag(regression: true));

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_TIMESTAMP_REGRESSION");
        Assert.Contains(result.Findings, f => f.Code == "NM_LOG_GAP_SUSPECTED");
    }

    [Fact]
    public void Analyze_MessageLostAcrossAllNmSilent_DoesNotReportPerMessageFaults()
    {
        // 0x500 于 1.0s 后消失；0x501 发到 1.5s 后全网静默，4.0s 才恢复。
        // 与全静默区间有交集的"没收到"不得归因到单个报文（网络级优先）。
        var nm = new List<NmFrame> { Nm(0, "0x500"), Nm(0.5, "0x500"), Nm(1.0, "0x500") };
        foreach (var t in new[] { 0.0, 0.5, 1.0, 1.5, 4.0, 4.5 }) nm.Add(Nm(t, "0x501"));
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 1.5), (4.0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_ALL_NM_SILENT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
    }

    [Fact]
    public void Analyze_SameNumericIdDifferentFormat_DoesNotMaskDropout()
    {
        // 标准 0x500 在 1.0s 后停发，扩展 0x500 一直发到 4.0s：
        // 两者是不同报文，扩展帧不能给标准帧的掉线当“仍在发送”的证据。
        var nm = new List<NmFrame>
        {
            Nm(0.0), Nm(0.5), Nm(1.0),
        };
        for (int i = 0; i <= 8; i++)
            nm.Add(Nm(i * 0.5, ext: true));
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        var dropout = Assert.Single(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        // 标准帧的结论用裸 ID；扩展帧若被误合并，这里会根本没有 dropout。
        Assert.Contains("0x500 从", dropout.Message);
        Assert.DoesNotContain("(ext)", dropout.Message);
    }

    [Fact]
    public void Analyze_BusWideSilenceEvent_UsesContainingEpisodeIndex()
    {
        // 事件发生在第二段活动内（t=2.0），编号必须是 1，而不是恒 0。
        var nm = new[] { Nm(0.0), Nm(0.5), Nm(2.0), Nm(4.0) };
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0.0, 0.5), (2.0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        var busWide = result.Events
            .Where(e => e.Type == "bus_wide_silence" && e.T == 2.0)
            .ToArray();
        Assert.Single(busWide);
        Assert.Equal(1, busWide[0].EpisodeIndex);
    }
}
