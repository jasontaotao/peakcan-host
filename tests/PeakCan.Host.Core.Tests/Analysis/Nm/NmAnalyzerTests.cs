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
    private static readonly string[] ConflictValues = ["500", "1000"];
    private static readonly string[] ConflictSources = ["dbc", "sidecar"];
    private static readonly string[] MissingDbc = ["dbc"];
    // CA1861: 共享空载荷，避免每个帧都分配新数组。
    private static readonly byte[] EmptyPayload = new byte[8];

    // §9.4 AfterContent 用例：报文带一个 8 位信号，用于区分内容事件与信号摘要事件。
    private const string SignalDbc = """
        VERSION ""

        BU_: ECU_A

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ S1 : 0|8@1+ (1,0) [0|255] "" ECU_A

        BA_DEF_ BO_ "MsgType" ENUM "CDD","CANNM";
        BA_ "MsgType" BO_ 1280 1;
        """;

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
    public void Analyze_EveryFinding_HasEvidenceInsideWindow()
    {
        var profile = Profile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true,
                    CannmReasons, 500, 2000, true),
                new NmMessageDef("0x501", false, "NM_Absent", "ECU_B", 8, true,
                    CannmReasons, 500, 2000, true),
            },
            Conflicts = new[]
            {
                new NmConfigConflict("CycleMs", ConflictValues, ConflictSources),
            },
        };
        var frames = Enumerable.Range(0, 10)
            .Select(i => F(i * 0.5, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 4.5));

        // 两类原先 EvidenceTimes 为空的 finding 都要覆盖到。
        Assert.Contains(report.Findings, f => f.Code == "NM_CONFIG_CONFLICT");
        Assert.Contains(report.Findings, f => f.Code == "NM_MESSAGE_ABSENT");
        Assert.All(report.Findings, finding =>
        {
            Assert.NotEmpty(finding.EvidenceTimes);
            Assert.All(finding.EvidenceTimes,
                t => Assert.InRange(t, report.Window.Start, report.Window.End));
        });
    }

    [Fact]
    public void Analyze_SharedIdWithMultipleNids_SuppressesCycleDeviation()
    {
        var profile = Profile() with { Layout = new NmLayout(NidByte: 0, CbvByte: 1) };
        var frames = new List<ReplayFrame>();
        for (int i = 0; i < 6; i++)
        {
            frames.Add(F(i * 0.5, 0x500, false, 0xF4, 0x00, 0, 0, 0, 0, 0, 0, 0));
            // 两个 NID 交错 100ms：合并后的间隔离散，按常规容差足以触发偏差判据。
            frames.Add(F(i * 0.5 + 0.1, 0x500, false, 0x27, 0x00, 0, 0, 0, 0, 0, 0));
        }

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 3.2));

        // 共享 ID 无法按节点拆周期，不得把交错发送误报成周期偏差。
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");
        Assert.Contains(report.Assumptions, a => a.Contains("共享 ID"));
    }

    [Fact]
    public void Analyze_FewCycleIntervals_ReportsInsufficientSamplesAssumption()
    {
        var frames = new[]
        {
            F(0.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
            F(1.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1.5));

        var message = Assert.Single(report.Messages);
        Assert.True(message.Cycle.InsufficientSamples);
        Assert.Contains(report.Assumptions,
            a => a.Contains("周期有效间隔") && a.Contains("< 5"));
    }

    [Fact]
    public void Analyze_ProfileDeclaresDbcMissing_AddsDbcMissingInput()
    {
        var profile = Profile() with { MissingFields = MissingDbc };
        var frames = new[] { F(0.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 1));

        Assert.Contains(report.MissingInputs, m => m.InputKey == "dbc");
    }

    [Fact]
    public void Analyze_PnDisabledWithCbv_AddsPniInferenceAssumption()
    {
        var profile = Profile() with
        {
            Layout = new NmLayout(NidByte: 0, CbvByte: 1),
            Features = new NmFeatures(PnEnabled: false),
        };
        var frames = new[]
        {
            F(0.0, 0x500, false, 0xF4, 0x10, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0xF4, 0x10, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 1));

        Assert.Contains(report.Assumptions,
            a => a.Contains("PNI 按 AUTOSAR CanNm CBV bit4 解读"));
        Assert.Contains(report.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }

    [Fact]
    public void Analyze_PopulatesSelectionDiagnostics()
    {
        var frames = new[]
        {
            F(0.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        Assert.NotNull(report.Selection);
        Assert.Equal(2, report.Selection!.DirectionUnknownFrames);
    }

    [Fact]
    public void Analyze_ReportsDirectionCountsFromFrames()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, EmptyPayload, FrameFlags.None, false, 0, IsTransmit: true),
            new ReplayFrame(0.5, 0x500, 8, EmptyPayload, FrameFlags.None, false, 0, IsTransmit: false),
            new ReplayFrame(1.0, 0x500, 8, EmptyPayload, FrameFlags.None, false, 0, IsTransmit: null),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1.5));

        Assert.NotNull(report.Direction);
        Assert.Equal(1, report.Direction!.Transmit);
        Assert.Equal(1, report.Direction.Receive);
        Assert.Equal(1, report.Direction.Unknown);
    }

    [Fact]
    public void Analyze_PreCancelledToken_ReturnsPartialReportWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // spec §9.4：取消不再抛异常，返回已完成阶段（选择/能力/缺项）的部分结果。
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, Array.Empty<ReplayFrame>(), null, Profile(), 0, 1), cts.Token);

        Assert.True(report.IsPartial);
        Assert.Empty(report.Messages);
        Assert.Empty(report.Episodes);
        Assert.NotEmpty(report.Capabilities);
        Assert.Contains(report.Assumptions, a => a.Contains("partial"));
        Assert.Contains(report.Unknowns, u => u.Contains("partial"));
    }

    [Fact]
    public void Analyze_CancelDuringMessageLoop_ReturnsProcessedMessagesAsPartial()
    {
        using var cts = new CancellationTokenSource();
        var messages = new[]
        {
            new NmMessageDef("0x500", false, "NM_1", "ECU_A", 8, true, CannmReasons, 500, 2000),
            new NmMessageDef("0x501", false, "NM_2", "ECU_A", 8, true, CannmReasons, 500, 2000),
            new NmMessageDef("0x502", false, "NM_3", "ECU_A", 8, true, CannmReasons, 500, 2000),
        };
        var profile = Profile() with { Messages = messages };
        var frames = new[]
        {
            F(0.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0), F(0.5, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0),
            F(0.0, 0x501, false, 0xF5, 0, 0, 0, 0, 0, 0, 0), F(0.5, 0x501, false, 0xF5, 0, 0, 0, 0, 0, 0, 0),
            F(0.0, 0x502, false, 0xF6, 0, 0, 0, 0, 0, 0, 0), F(0.5, 0x502, false, 0xF6, 0, 0, 0, 0, 0, 0, 0),
        };
        var request = new NmAnalysisRequest("src", null, frames, null, profile, 0, 1);

        // 第二次进入逐报文循环时触发取消：第 1 条报文已完成，第 2/3 条不应出现在报告里。
        var loopChecks = 0;
        var report = NmAnalyzer.Analyze(request, checkpoint =>
        {
            if (checkpoint == NmAnalyzerCheckpoint.MessageLoop && ++loopChecks == 2)
                cts.Cancel();
        }, cts.Token);

        Assert.True(report.IsPartial);
        // 只保留已处理的第 1 条报文（0x500），未处理的报文不得产生任何结论。
        var processed = Assert.Single(report.Messages);
        Assert.Equal("0x500", processed.CanIdHex);
        Assert.DoesNotContain(report.Findings, f => f.Message.Contains("0x501") || f.Message.Contains("0x502"));
        Assert.Contains(report.Assumptions, a => a.Contains("partial"));
    }

    [Fact]
    public void Analyze_CancelAfterEpisodes_KeepsEpisodesButNoMessageReports()
    {
        using var cts = new CancellationTokenSource();
        var frames = new[]
        {
            F(0.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0),
            F(1.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("src", null, frames, null, Profile(), 0, 2.0),
            checkpoint =>
            {
                if (checkpoint == NmAnalyzerCheckpoint.AfterEpisodes) cts.Cancel();
            },
            cts.Token);

        Assert.True(report.IsPartial);
        Assert.Single(report.Episodes);
        Assert.Empty(report.Messages);
        Assert.Empty(report.Findings);
        Assert.Empty(report.Events);
    }

    [Fact]
    public void Analyze_CancelAfterGap_KeepsGapEventsButSkipsContent()
    {
        using var cts = new CancellationTokenSource();
        var frames = new[]
        {
            F(0.0, 0x500, false, 0x00, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0x01, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("src", null, frames, null, Profile(), 0, 5.0),
            checkpoint =>
            {
                if (checkpoint == NmAnalyzerCheckpoint.AfterGap) cts.Cancel();
            },
            cts.Token);

        Assert.True(report.IsPartial);
        Assert.Single(report.Messages);
        // 空档阶段已跑完：尾部全网络静默事件在报告里。
        Assert.Contains(report.Events, e => e.Type == "all_nm_silent");
        // 内容阶段未跑：字节变化事件不应凭空出现。
        Assert.DoesNotContain(report.Events, e => e.Type == "byte_change");
        Assert.Empty(report.Truncations);
    }

    [Fact]
    public void Analyze_CancelAfterContent_SkipsSignalEvents()
    {
        using var cts = new CancellationTokenSource();
        var dbc = DbcParser.Parse(SignalDbc).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);
        var frames = new[]
        {
            F(0.0, 0x500, false, 0x00, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0x01, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("src", null, frames, dbc, profile, 0, 1.0),
            checkpoint =>
            {
                if (checkpoint == NmAnalyzerCheckpoint.AfterContent) cts.Cancel();
            },
            cts.Token);

        Assert.True(report.IsPartial);
        // 内容阶段已跑完：字节变化事件在报告里。
        Assert.Contains(report.Events, e => e.Type == "byte_change");
        // 信号摘要阶段未跑：DBC 信号变化事件不应出现。
        Assert.DoesNotContain(report.Events, e => e.Type == "signal_change");
    }

    [Fact]
    public void Analyze_NotCancelled_IsNotPartial()
    {
        var frames = new[] { F(0.0, 0x500, false, 0xF4, 0, 0, 0, 0, 0, 0, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        Assert.False(report.IsPartial);
        Assert.DoesNotContain(report.Assumptions, a => a.Contains("partial"));
    }

    [Fact]
    public void Analyze_SignalChangeInSecondEpisode_CarriesThatEpisodeIndex()
    {
        // 0.5s 与 3.0s 之间的静默 ≥ 2s，切出两个 episode；
        // 第二段里的 signal_change（T=3.0、3.5）必须带 episode_index=1。
        var dbc = DbcParser.Parse(SignalDbc).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);
        var frames = new[]
        {
            F(0.0, 0x500, false, 0x00, 0, 0, 0, 0, 0, 0, 0),
            F(0.5, 0x500, false, 0x01, 0, 0, 0, 0, 0, 0, 0),
            F(3.0, 0x500, false, 0x02, 0, 0, 0, 0, 0, 0, 0),
            F(3.5, 0x500, false, 0x03, 0, 0, 0, 0, 0, 0, 0),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, dbc, profile, 0, 4.0));

        Assert.Equal(2, report.Episodes.Count);
        var secondEpisode = Assert.Single(report.Events,
            e => e.Type == "signal_change" && e.T == 3.0);
        Assert.Equal(1, secondEpisode.EpisodeIndex);
        // 第一段的信号事件仍是 0，佐证编号不是恒 1。
        Assert.Equal(0, Assert.Single(report.Events,
            e => e.Type == "signal_change" && e.T == 0.5).EpisodeIndex);
    }
}
