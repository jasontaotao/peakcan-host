using FluentAssertions;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class UdsOfflineAnalyzerTests
{
    private static readonly UdsOfflineAddress Addr = new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    private static ReplayFrame Frame(uint id, params byte[] data)
        => FrameAt(0, id, data);

    private static ReplayFrame FrameAt(double ts, uint id, params byte[] data)
        => new(Timestamp: ts, Id: id, Dlc: (byte)data.Length, Data: data,
               Flags: PeakCan.HIL.Core.FrameFlags.None, IsExtended: false, Channel: 1, IsTransmit: null);

    private static UdsOfflineDiagnostics DefaultDiag(IReadOnlyList<DidDefinition>? dids = null)
        => new(dids ?? Array.Empty<DidDefinition>(), Array.Empty<RoutineDefinition>(),
               OdxLoaded: false, UserJsonLoadStatus.NotConfigured, UserJsonLoadStatus.NotConfigured, "snap");

    private static UdsOfflineAnalysisRequest Request(params ReplayFrame[] frames)
        => Request(DefaultDiag(), frames);

    private static UdsOfflineAnalysisRequest Request(UdsOfflineDiagnostics diag, params ReplayFrame[] frames)
        => new(Guid.NewGuid(), "src", "C:/logs/test.asc", frames, Addr, diag, "test-engine");

    [Fact]
    public void Mixed_log_satisfies_count_closure_and_keeps_noise_evidence()
    {
        var req = Request(
            Frame(0x7E0, 0x02, 0x10, 0x01),
            Frame(0x123, 0xAA),                                        // non-diagnostic
            Frame(0x7E0, 0x0F, 0x10),                                  // invalid PCI
            Frame(0x7E8, 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4));   // 合法正响应

        var report = new UdsOfflineAnalyzer().Analyze(req);

        report.Counts.Closes.Should().BeTrue();
        report.Counts.NonDiagnostic.Should().Be(1);
        report.Counts.Noise.Should().Be(1);
        report.Counts.MatchedDiagnostic.Should().Be(2);
        report.Noise.TopIds.Should().Contain(x => x.Id == 0x123 && x.Count == 1);
        report.Transactions.Should().ContainSingle();
        report.IsPartial.Should().BeFalse();
    }

    [Fact]
    public void Pre_cancelled_token_returns_partial_report_with_completed_stages()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = new UdsOfflineAnalyzer().Analyze(Request(Frame(0x7E0, 0x02, 0x10, 0x01)), cts.Token);
        report.IsPartial.Should().BeTrue();
        report.CompletedStages.Should().Contain("classify");
        report.Transactions.Should().BeEmpty();
    }

    [Fact]
    public void Report_is_immutable_snapshot_of_definitions()
    {
        var did = new DidDefinition(0xF190, "VIN", "", 17, false);
        var diag = DefaultDiag(new[] { did });
        var report = new UdsOfflineAnalyzer().Analyze(Request(diag,
            Frame(0x7E0, 0x03, 0x22, 0xF1, 0x90),
            // 响应 20 字节：0x62 F1 90 + 17 字节 ASCII，用 FF + 2×CF 传
            Frame(0x7E8, 0x10, 0x14, 0x62, 0xF1, 0x90, 0x41, 0x41, 0x41),
            Frame(0x7E8, 0x21, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41),
            Frame(0x7E8, 0x22, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41)));

        report.Transactions.Should().ContainSingle();
        report.Transactions[0].FinalKind.Should().Be(UdsFinalKind.Positive);
        report.Diagnostics.Dids.Should().ContainSingle();
        report.Counts.Closes.Should().BeTrue();
    }

    [Fact]
    public void Validation_failure_is_reclassified_as_noise_and_counted()
    {
        // 0x36 请求块序号 1，响应 0x76 回显 2 → 校验失败，响应帧进噪声桶。
        var report = new UdsOfflineAnalyzer().Analyze(Request(
            Frame(0x7E0, 0x04, 0x36, 0x01, 0xAA, 0xBB),
            Frame(0x7E8, 0x02, 0x76, 0x02)));

        report.Noise.ResponseValidationFailed.Should().Be(1);
        report.Counts.Closes.Should().BeTrue();
        report.Counts.Noise.Should().BeGreaterThanOrEqualTo(1);
        report.Transactions.Should().ContainSingle()
            .Which.FinalKind.Should().Be(UdsFinalKind.None);
    }

    [Fact]
    public void Orphan_consecutive_frame_is_reported_with_its_own_reason()
    {
        var report = new UdsOfflineAnalyzer().Analyze(Request(
            Frame(0x7E0, 0x21, 0x01, 0x02)));

        report.Noise.Reasons.Should().Contain(r =>
            r.Reason == UdsNoiseReason.OrphanConsecutiveFrame && r.Count == 1);
        report.Noise.Reasons.Should().NotContain(r => r.Reason == UdsNoiseReason.ResponseValidationFailed,
            "日志从中段开始不是响应校验失败");
    }

    [Fact]
    public void Report_exposes_session_timing_and_flags_late_response()
    {
        var report = new UdsOfflineAnalyzer().Analyze(Request(
            FrameAt(0.0, 0x7E0, 0x02, 0x10, 0x01),
            FrameAt(0.010, 0x7E8, 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4),
            FrameAt(10.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            FrameAt(115.0, 0x7E8, 0x04, 0x62, 0xF1, 0x90, 0x31)));

        report.Timing.Should().NotBeNull("ECU 声明的 P2/P2* 必须进入报告");
        report.Timing!.P2Ms.Should().Be(50);
        report.Timing.P2StarMs.Should().Be(5000);

        var late = report.Transactions.Single(t => t.Request.Payload[0] == 0x22);
        late.ExceedsP2.Should().BeTrue("105 秒远超 P2，只做标记不剔除");
        late.FinalKind.Should().Be(UdsFinalKind.Positive);
    }

    [Fact]
    public void Empty_log_produces_closed_zero_counts_and_no_crash()
    {
        var report = new UdsOfflineAnalyzer().Analyze(Request());
        report.Counts.Input.Should().Be(0);
        report.Counts.Closes.Should().BeTrue();
        report.Transactions.Should().BeEmpty();
        report.LogStart.Should().Be(0);
        report.LogEnd.Should().Be(0);
    }
}
