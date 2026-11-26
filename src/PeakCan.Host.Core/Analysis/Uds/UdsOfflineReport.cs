namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Immutable result of one analysis run. Everything the UI, exports and the AI
/// handoff show comes from this one object — no view re-reads the log or the
/// definition database, so all three surfaces agree byte for byte.
/// <para>
/// <see cref="IsPartial"/> is true when the run was cancelled: counts still
/// reflect the frames classified so far, <see cref="CompletedStages"/> names the
/// stages that finished, and <see cref="PartialReason"/> says where it stopped.
/// A partial report must never be presented as a complete analysis.
/// </para>
/// </summary>
public sealed record UdsOfflineReport(
    Guid ReportId,
    string SourceId,
    string SourcePath,
    UdsOfflineAddress Address,
    UdsOfflineDiagnostics Diagnostics,
    string EngineVersion,
    double LogStart,
    double LogEnd,
    UdsFrameCounts Counts,
    UdsNoiseSummary Noise,
    IReadOnlyList<UdsFrameEvidence> Evidence,
    IReadOnlyList<UdsOfflineTransaction> Transactions,
    IReadOnlyList<UdsOfflinePdu> OrphanResponses,
    IReadOnlyList<UdsTransportWarning> Warnings,
    bool IsPartial,
    string? PartialReason,
    IReadOnlyList<string> CompletedStages)
{
    /// <summary>ECU 在会话控制响应里声明的 P2 / P2*；未观测到时为 null。</summary>
    public UdsSessionTiming? Timing { get; init; }
}
