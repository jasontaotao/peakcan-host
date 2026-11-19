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

    private static NmFrame Nm(double t, string id = "0x500", byte nid = 0xF4)
        => new(new NmMessageDef(id, false, "NM", "A", 8, true, TestReasons, 500, 2000),
               new ReplayFrame(t, CanId(id), 2, new byte[] { nid, 0 }, FrameFlags.None));

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
}
