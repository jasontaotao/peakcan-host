using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmAnalyzerTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static ReplayFrame F(double t, uint id, bool ext, params byte[] data)
        => new(t, id, (byte)data.Length, data, FrameFlags.None, ext);

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true,
            CannmReasons, 500, 2000, true) },
        new NmTimings(500, 2000, 1600, 2000),
        new[] { new NmNodeDef("ECU_A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    [Fact]
    public void Analyze_RegularNmStream_ProducesEpisodesMessagesAndCapabilities()
    {
        var frames = Enumerable.Range(0, 41)
            .Select(i => F(i * 0.5, 0x500, false, 0xF4, 0x00, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 20.5));

        Assert.Single(report.Episodes);
        var msg = Assert.Single(report.Messages);
        Assert.Equal(41, msg.FrameCount);
        Assert.Equal(500, msg.Cycle.MedianMs);
        Assert.Equal(NmAnalyzer.EngineVersion, report.EngineVersion);
        Assert.Contains(report.Capabilities, c => c.Capability == NmCapability.IdentifyMessages && c.Available);
        Assert.Contains(report.Unknowns, u => u.Contains("bus_sleep"));
    }

    [Fact]
    public void Analyze_ConfiguredNmMessageAbsent_ProducesMessageAbsent()
    {
        var frames = new[] { F(0, 0x100, false, 0, 0, 0, 0, 0, 0, 0, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        Assert.Contains(report.Findings, f => f.Code == "NM_MESSAGE_ABSENT");
    }

    [Fact]
    public void Analyze_DlcMismatch_ProducesFinding()
    {
        var frames = new[] { F(0, 0x500, false, 0xF4, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        Assert.Contains(report.Findings, f => f.Code == "NM_DLC_MISMATCH");
    }

    [Fact]
    public void Analyze_NoNmFrames_ReturnsEmptyWithMissingInputs()
    {
        var frames = new[] { F(0, 0x100, false, 0, 0, 0, 0, 0, 0, 0, 0) };
        var profile = Profile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, false, Array.Empty<string>()),
            },
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 1));

        Assert.Empty(report.Messages);
        Assert.Contains(report.MissingInputs, m => m.InputKey == "nm_message_selection");
        Assert.Contains("该窗口", string.Join(" ", report.Assumptions));
    }

    [Fact]
    public void Analyze_Cancellation_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NmAnalyzer.Analyze(new NmAnalysisRequest(
                "src", null, Array.Empty<ReplayFrame>(), null, Profile(), 0, 1), cts.Token));
    }
}
