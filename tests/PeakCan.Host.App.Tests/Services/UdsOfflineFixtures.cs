using PeakCan.HIL.Core.Uds.Database;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// 报告导出测试用的固定快照。刻意构造闭合计数，避免导出测试掩盖口径问题。
/// </summary>
internal static class UdsOfflineFixtures
{
    private static readonly UdsOfflineAddress Address = new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    public static UdsOfflineReport Report()
        => Build(
            transactions: new[] { ReadDidTransaction() },
            isPartial: false,
            partialReason: null);

    /// <summary>无 ODX 的普通报告（AI 提示词测试用）。</summary>
    public static UdsOfflineReport ReportNoOdx() => Report();

    public static UdsOfflineReport ReportWithSensitiveData()
        => Build(
            transactions: new[] { SecurityAccessTransaction(), ReadDidTransaction() },
            isPartial: false,
            partialReason: null);

    /// <summary>归属歧义 + 重复最终响应的事务（导出/界面必须标注）。</summary>
    public static UdsOfflineReport ReportWithAmbiguity()
        => Build(
            transactions: new[]
            {
                ReadDidTransaction() with { IsAmbiguous = true, HasDuplicateFinalResponse = true },
            },
            isPartial: false,
            partialReason: null);

    /// <summary>未知 SID 事务（UnknownService 标记）。</summary>
    public static UdsOfflineReport ReportWithUnknownService()
        => Build(
            transactions: new[]
            {
                ReadDidTransaction() with
                {
                    Interpretation = new UdsInterpretation("SID 0xF0", null, null, null, null, null)
                    {
                        IsUnknownService = true,
                    },
                },
            },
            isPartial: false,
            partialReason: null);

    public static UdsOfflineReport PartialReport()
        => Build(
            transactions: Array.Empty<UdsOfflineTransaction>(),
            isPartial: true,
            partialReason: "cancelled at pair");

    private static UdsOfflineReport Build(
        IReadOnlyList<UdsOfflineTransaction> transactions,
        bool isPartial,
        string? partialReason)
    {
        var diag = new UdsOfflineDiagnostics(
            new[] { new DidDefinition(0xF190, "VIN", "Vehicle Identification Number", 8, false) },
            Array.Empty<RoutineDefinition>(),
            OdxLoaded: false,
            DidUserJsonStatus: UserJsonLoadStatus.Missing,
            RoutineUserJsonStatus: UserJsonLoadStatus.NotConfigured,
            SnapshotId: "snap-1");

        var counts = new UdsFrameCounts(Input: 6, MatchedDiagnostic: 3, NonDiagnostic: 2, Noise: 1, UnsupportedInput: 0);
        var noise = new UdsNoiseSummary(
            TopIds: new[] { new UdsNoiseIdCount(0x123, IsExtended: false, Count: 2, FirstEvidenceIndex: 1, LastEvidenceIndex: 2) },
            Reasons: new[] { new UdsNoiseReasonCount(UdsNoiseReason.InvalidPci, 1) },
            ResponseValidationFailed: 0,
            DuplicateFinalResponses: 0,
            AggregationTruncated: false,
            DistinctNonDiagnosticIds: 1);

        var evidence = new[]
        {
            new UdsFrameEvidence(0, 0.0, 1, 0x7E0, false, null,
                UdsFrameClass.MatchedDiagnostic, null, "SingleFrame", 4, new byte[] { 0x03, 0x22, 0xF1, 0x90 }),
        };

        return new UdsOfflineReport(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "src-1",
            "C:/logs/sensitive.asc",
            Address,
            diag,
            "test-engine",
            LogStart: 0.0,
            LogEnd: 1.5,
            counts,
            noise,
            evidence,
            transactions,
            Array.Empty<UdsOfflinePdu>(),
            Warnings,
            isPartial,
            partialReason,
            Stages)
        {
            Timing = new UdsSessionTiming(50, 5000, "ECU:0x50"),
        };
    }

    private static readonly UdsTransportWarning[] Warnings = { new(3, "SequenceError") };
    private static readonly string[] Stages = { "classify", "reassemble", "pair", "timing", "interpret" };

    private static UdsOfflinePdu Pdu(UdsDirectionRole role, int index, params byte[] payload)
        => new(role, payload, index * 0.1, index * 0.1, index, index, new[] { index }, false, true, Array.Empty<string>());

    private static readonly UdsOfflinePdu ReadDidRequest = Pdu(UdsDirectionRole.Request, 0, 0x22, 0xF1, 0x90);
    private static readonly UdsOfflinePdu[] ReadDidResponses =
    {
        Pdu(UdsDirectionRole.Response, 1, 0x62, 0xF1, 0x90, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48),
    };
    private static readonly UdsOfflinePdu SecurityAccessRequest =
        Pdu(UdsDirectionRole.Request, 2, 0x27, 0x01, 0x11, 0x22, 0x33, 0x44);
    private static readonly UdsOfflinePdu[] SecurityAccessResponses =
    {
        Pdu(UdsDirectionRole.Response, 3, 0x67, 0x01, 0xAA, 0xBB),
    };

    private static UdsOfflineTransaction ReadDidTransaction() => new(
        Request: ReadDidRequest,
        Responses: ReadDidResponses,
        FinalKind: UdsFinalKind.Positive,
        PendingCount: 0,
        FinalNrc: null,
        IsOrphan: false,
        IsAmbiguous: false,
        HasDuplicateFinalResponse: false,
        Unknowns: Array.Empty<string>())
    {
        FirstResponseMs = 10.0,
        TotalMs = 10.0,
        TimingSource = "Unknown",
        Interpretation = new UdsInterpretation(
            "ReadDataByIdentifier", "VIN", null, "VIN=ABCDEFGH", null, null),
    };

    private static UdsOfflineTransaction SecurityAccessTransaction() => new(
        Request: SecurityAccessRequest,
        Responses: SecurityAccessResponses,
        FinalKind: UdsFinalKind.Positive,
        PendingCount: 0,
        FinalNrc: null,
        IsOrphan: false,
        IsAmbiguous: false,
        HasDuplicateFinalResponse: false,
        Unknowns: Array.Empty<string>())
    {
        FirstResponseMs = 5.0,
        TotalMs = 5.0,
        TimingSource = "Unknown",
        Interpretation = new UdsInterpretation("SecurityAccess", null, null, null, null, null),
    };
}
