using System.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.HIL.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// <c>diagnose</c> 阶段接进报告后，端到端行为必须符合设计文档验收标准。
/// </summary>
public sealed class UdsOfflineDiagnoseStageTests
{
    private static readonly UdsOfflineAddress Addr =
        new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    private static readonly UdsOfflineDiagnostics Diag = new(
        Array.Empty<DidDefinition>(),
        Array.Empty<RoutineDefinition>(),
        OdxLoaded: false,
        UserJsonLoadStatus.NotConfigured,
        UserJsonLoadStatus.NotConfigured,
        SnapshotId: "snap");

    private static ReplayFrame Frame(double ts, uint id, params byte[] data)
        => new(ts, id, (byte)data.Length, data, PeakCan.HIL.Core.FrameFlags.None,
            IsExtended: false, Channel: 1, IsTransmit: true);

    private static UdsOfflineReport Analyze(params ReplayFrame[] frames)
        => new UdsOfflineAnalyzer().Analyze(new UdsOfflineAnalysisRequest(
            Guid.NewGuid(), "test.asc", "test.asc", frames, Addr, Diag, "test"));

    private static byte[] SessionResponse(byte session)
        => new byte[] { 0x06, 0x50, session, 0x00, 0x32, 0x01, 0xF4 };

    /// <summary>验收标准第 2 条：末尾 0x10 01 成功 → 报告说「最后确认处于默认会话」。</summary>
    [Fact]
    public void Report_ends_at_the_last_confirmed_session()
    {
        var report = Analyze(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, SessionResponse(0x03)),
            Frame(1.0, 0x7E0, 0x02, 0x10, 0x01),
            Frame(1.01, 0x7E8, SessionResponse(0x01)));

        report.SessionTimeline.Should().NotBeEmpty();
        report.SessionTimeline[^1].SessionType.Should().Be(0x01);
        report.SessionTimeline[^1].Observation.Should().Be(UdsSessionObservation.Confirmed);
        report.CompletedStages.Should().Contain("diagnose");
    }

    /// <summary>验收标准第 3 条：日志从传输中途开始 → 首项必须是 UnknownAtLogStart。</summary>
    [Fact]
    public void Log_starting_mid_session_reports_unknown_at_start()
    {
        var report = Analyze(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));

        report.SessionTimeline[0].Observation.Should().Be(UdsSessionObservation.UnknownAtLogStart);
        report.SessionTimeline[0].SessionType.Should().BeNull();
    }

    /// <summary>验收标准第 1 条：两个独立异常不能被合并成一条链。</summary>
    [Fact]
    public void Two_independent_failures_produce_two_findings()
    {
        var report = Analyze(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, SessionResponse(0x03)),
            Frame(0.1, 0x7E0, 0x02, 0x27, 0x01),
            Frame(0.11, 0x7E8, 0x02, 0x67, 0x01),
            Frame(0.2, 0x7E0, 0x04, 0x27, 0x02, 0x11, 0x22),
            Frame(0.21, 0x7E8, 0x03, 0x7F, 0x27, 0x35),
            Frame(0.3, 0x7E0, 0x03, 0x34, 0x00, 0x44),
            Frame(0.31, 0x7E8, 0x04, 0x74, 0x20, 0x04, 0x02),
            Frame(0.4, 0x7E0, 0x03, 0x36, 0x01, 0xAA),
            Frame(0.41, 0x7E8, 0x02, 0x76, 0x01),
            Frame(0.5, 0x7E0, 0x03, 0x36, 0x02, 0xBB),
            Frame(0.51, 0x7E8, 0x03, 0x7F, 0x36, 0x73));

        var negatives = report.Findings.Where(f => f.Code == "NEGATIVE_RESPONSE").ToArray();
        negatives.Should().HaveCount(2);
        negatives.Select(f => f.Suspect).Should()
            .BeEquivalentTo(new[] { UdsSuspect.ClientParameters, UdsSuspect.ClientParameters });
        report.FindingLinks.Should().BeEmpty("P0 不生成关联");
    }

    /// <summary>验收标准第 4 条：缺 0x37 时 FlashFlow.Completed == false 且 BlockingStage 非空。</summary>
    [Fact]
    public void Missing_transfer_exit_surfaces_as_flash_incomplete()
    {
        var report = Analyze(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, SessionResponse(0x03)),
            Frame(0.1, 0x7E0, 0x03, 0x34, 0x00, 0x44),
            Frame(0.11, 0x7E8, 0x04, 0x74, 0x20, 0x04, 0x02),
            Frame(0.2, 0x7E0, 0x03, 0x36, 0x01, 0xAA),
            Frame(0.21, 0x7E8, 0x02, 0x76, 0x01));

        report.FlashFlow.Should().NotBeNull();
        report.FlashFlow!.Completed.Should().BeFalse();
        report.FlashFlow.BlockingStage.Should().Be(UdsFlashStageKind.RequestTransferExit);
        report.Findings.Should().Contain(f => f.Code == "FLASH_INCOMPLETE");
    }

    /// <summary>验收标准第 10 条：部分报告的 Findings 必须是空数组，不能是半成品。</summary>
    [Fact]
    public void Partial_report_has_no_findings()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = new UdsOfflineAnalyzer().Analyze(new UdsOfflineAnalysisRequest(
            Guid.NewGuid(), "t.asc", "t.asc",
            new[] { Frame(0.0, 0x7E0, 0x02, 0x10, 0x03) },
            Addr, Diag, "test"), cts.Token);

        report.IsPartial.Should().BeTrue();
        report.Findings.Should().BeEmpty();
        report.CompletedStages.Should().NotContain("diagnose");
    }

    /// <summary>验收标准第 9 条：无异常时结论区不能留空。</summary>
    [Fact]
    public void Clean_log_yields_empty_findings_which_ui_renders_as_no_anomaly()
    {
        var report = Analyze(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));

        report.Findings.Should().BeEmpty();
        report.Observations.Should().BeEmpty();
    }

    /// <summary>
    /// 端到端：请求之后日志还在继续记，报告必须指向 ECU 而不是日志。
    /// <para>
    /// 这条守的是「<c>logEnd</c> 真的从分析器传到了结论生成器」——
    /// 单测里我直接传参，无法证明生产路径也传了。
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>端到端验证「测试仪停发」的归因。</b>
    /// <para>
    /// 这条测试在 2026-11-27 被改写。旧版本断言 <c>EcuInternalState</c>，
    /// 理由是「日志又记了 30 秒，ECU 有时间回却没回」——但 fixture 里请求之后
    /// <b>没有任何测试仪活动</b>，日志末尾全是其它 ECU 的背景报文。那正是
    /// 「测试仪停了」的形态，不是 ECU 的错。生产日志 <c>150635_log.asc</c>
    /// 就是被这个错误判据坑了。
    /// </para>
    /// </summary>
    [Fact]
    public void Unclosed_request_after_the_tester_stopped_is_attributed_to_the_tester_end_to_end()
    {
        var frames = new List<ReplayFrame>
        {
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
        };
        // 请求之后测试仪再没发过任何东西，只有其它 ECU 的背景报文。
        for (var i = 1; i <= 300; i++)
            frames.Add(Frame(i * 0.1, 0x200, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));

        var report = Analyze(frames.ToArray());

        var unclosed = report.Findings.Should().Contain(f => f.Code == "UNCLOSED_REQUEST").Subject;
        unclosed.Suspect.Should().Be(UdsSuspect.ClientSequencing,
            "请求之后测试仪一帧都没再发——ECU 没有机会回");
        unclosed.ObservedAction.Should().Be(UdsObservedAction.ClientStopped);
    }

    /// <summary>
    /// 对照用例：测试仪在请求之后仍在工作（发了别的请求），ECU 没回 → 这才是 ECU 的问题。
    /// 两条必须给出不同的归因，否则「测试仪停发」永远无法被识别。
    /// </summary>
    [Fact]
    public void Unclosed_request_while_the_tester_kept_working_blames_the_ecu_end_to_end()
    {
        var frames = new List<ReplayFrame>
        {
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),      // 未闭合
            // 用不同 SID 证明测试仪仍在工作，避开「同 SID 重叠取最旧」的已知配对缺陷。
            Frame(5.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(5.01, 0x7E8, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4),
        };
        for (var i = 60; i <= 300; i++)
            frames.Add(Frame(i * 0.1, 0x200, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));

        var report = Analyze(frames.ToArray());

        var unclosed = report.Findings.Should().Contain(f => f.Code == "UNCLOSED_REQUEST").Subject;
        unclosed.Suspect.Should().Be(UdsSuspect.EcuInternalState,
            "测试仪一直工作到 5s，日志记到 30s，ECU 有的是机会回");
    }

    /// <summary>验收标准第 7 条：ValidationFailures 的 FailedCheck 来自校验器实际返回值。</summary>
    [Fact]
    public void Validation_failure_details_reach_the_report()
    {
        var report = Analyze(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x91));

        report.ValidationFailures.Should().ContainSingle()
            .Which.FailedCheck.Should().Be("DidEcho");
    }
}
