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

    private static ReplayFrame FOn(double t, uint id, bool ext, ushort channel, params byte[] data)
        => new(t, id, (byte)data.Length, data, FrameFlags.None, ext, channel);

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
    public void Analyze_MultiChannelWithoutSelection_SkipsAnalysisWithoutAbsentFindings()
    {
        var frames = new[]
        {
            FOn(0, 0x500, false, 1, 0xF4, 0),
            FOn(0.5, 0x500, false, 2, 0xF4, 0),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        // 多通道短路后没有可分析的帧，不能再断言"报文缺失"。
        Assert.Empty(report.Messages);
        Assert.Empty(report.Episodes);
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_MESSAGE_ABSENT");
        Assert.Contains(report.Assumptions, a => a.Contains("跳过分析"));
    }

    [Fact]
    public void Analyze_SameNumericIdDifferentFrameFormat_ReportsSeparately()
    {
        var messages = new[]
        {
            new NmMessageDef("0x500", false, "NM_Std", "ECU_A", 8, true, CannmReasons, 500, 2000),
            new NmMessageDef("0x500", true, "NM_Ext", "ECU_A", 8, true, CannmReasons, 500, 2000),
        };
        var profile = Profile() with { Messages = messages };
        var frames = new[]
        {
            F(0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, true, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 1));

        Assert.Equal(2, report.Messages.Count);
        var standard = report.Messages.Single(m => !m.IsExtended);
        var extended = report.Messages.Single(m => m.IsExtended);
        Assert.Equal("NM_Std", standard.Name);
        Assert.Equal(1, standard.FrameCount);
        Assert.Equal("NM_Ext", extended.Name);
        Assert.Equal(1, extended.FrameCount);
    }

    [Fact]
    public void Analyze_DuplicateMessageDefinition_SkipsDuplicateWithoutThrowing()
    {
        var messages = new[]
        {
            new NmMessageDef("0x500", false, "NM_First", "ECU_A", 8, true, CannmReasons, 500, 2000),
            new NmMessageDef("0x500", false, "NM_Dup", "ECU_A", 8, true, CannmReasons, 500, 2000),
        };
        var profile = Profile() with { Messages = messages };
        var frames = new[] { F(0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 1));

        var message = Assert.Single(report.Messages);
        Assert.Equal("NM_First", message.Name);
        Assert.Contains(report.Assumptions, a => a.Contains("报文定义重复"));
    }

    [Fact]
    public void Analyze_SignalEventsExceedLimit_TruncatesPerMessage()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ SigA : 0|8@1+ (1,0) [0|255] "" ECU_B
 SG_ SigB : 8|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;

        // 120 帧、两个信号每帧都变化 → 238 条 signal_change，超过单报文 200 上限。
        var frames = Enumerable.Range(0, 120)
            .Select(i => F(i * 0.5, 0x500, false,
                (byte)i, (byte)(255 - i), 0, 0, 0, 0, 0, 0))
            .ToArray();

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, dbc, Profile(), 0, 60));

        var truncation = Assert.Single(
            report.Truncations, t => t.Section == "events:signals:0x500");
        Assert.Equal(200, truncation.Returned);
        Assert.Equal(238, truncation.Total);
        Assert.True(report.Events.Count(e => e.Type == "signal_change" && e.CanIdHex == "0x500") <= 200);
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
