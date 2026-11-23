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
