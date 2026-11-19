using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §13.2 M1 fixture 1–7 的等价内联场景。ASC 文件版本留给集成测试，
/// 这里锁定引擎行为，不依赖文件 IO。
/// </summary>
public class NmAnalyzerFixtureTests
{
    private static readonly string[] s_nmReasons = ["MsgType=CANNM"];

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true,
            s_nmReasons, 500, 2000, true) },
        new NmTimings(500, 2000, 1600, 2000, 50, 5),
        new[] { new NmNodeDef("BMS", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    private static ReplayFrame F(double t, uint id = 0x18FFFAF4, int dlc = 8)
        => new(t, id, (byte)dlc, Enumerable.Repeat((byte)0xF4, dlc).ToArray(), FrameFlags.None, true);

    private static NmReport Run(IReadOnlyList<ReplayFrame> frames, double end)
        => NmAnalyzer.Analyze(new NmAnalysisRequest("src", null, frames, null, Profile(), 0, end));

    [Fact]
    public void Fixture1_TimingBreakAndBurst()
    {
        var frames = new List<ReplayFrame>();
        for (int i = 0; i < 5; i++) frames.Add(F(i * 0.05));
        for (int i = 1; i <= 8; i++) frames.Add(F(0.2 + i * 0.5));
        // 尾段必须与 4.2s 的最后一帧间隔 ≥ 2s（超时），否则切不出第二个 episode，
        // 也就不会有 NM_ALL_NM_SILENT。5.0s 只有 0.8s 间隔，切不开。
        for (int i = 0; i < 5; i++) frames.Add(F(7.0 + i * 0.05));

        var report = Run(frames, 7.2);

        Assert.Contains(report.Events, e => e.Type == "burst_observed");
        Assert.Equal(2, report.Episodes.Count);
        Assert.Contains(report.Findings, f => f.Code == "NM_ALL_NM_SILENT");
    }

    [Fact]
    public void Fixture3_BoundaryMarked()
    {
        var frames = new[] { F(0), F(0.5), F(1.0) };

        var report = Run(frames, 1.0);

        Assert.True(report.Episodes[0].BoundaryStart);
        Assert.True(report.Episodes[0].BoundaryEnd);
        Assert.Contains(report.Findings, f => f.Code == "NM_TRACE_BOUNDARY");
    }

    [Fact]
    public void Fixture4_RawContentEventsWithoutNidConclusions()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x18FFFAF4, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, true),
            new ReplayFrame(0.5, 0x18FFFAF4, 8, new byte[] { 0x27, 0x08, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, true),
        };

        // 窗口是半开区间 [Start, End)：end=0.5 会把 t=0.5 的第二帧丢掉，
        // 只剩一帧就产不出 byte/bit 变化事件。取 0.6 让两帧都落在窗口内。
        var report = Run(frames, 0.6);

        Assert.Contains(report.Events, e => e.Type == "byte_change");
        Assert.Contains(report.Events, e => e.Type == "bit_change");
        Assert.DoesNotContain(report.Findings,
            f => f.Code.StartsWith("NM_NID", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }

    [Fact]
    public void Fixture6_AbsentAndDlcMismatch()
    {
        var present = new[] { F(0, dlc: 4), F(0.5, dlc: 4) };

        var report = Run(present, 1.0);

        Assert.Contains(report.Findings, f => f.Code == "NM_DLC_MISMATCH");
    }

    [Fact]
    public void Fixture7_NoNm_ReturnsEmptyAndExplains()
    {
        var frames = new[] { new ReplayFrame(0, 0x100, 8, new byte[8], FrameFlags.None) };
        var profile = Profile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest("src", null, frames, null, profile, 0, 1));

        Assert.Empty(report.Messages);
        Assert.Contains(report.Assumptions, a => a.Contains("未发现 NM 帧"));
        Assert.DoesNotContain(report.Findings,
            f => f.Code.StartsWith("NM_NODE", StringComparison.Ordinal));
    }
}
