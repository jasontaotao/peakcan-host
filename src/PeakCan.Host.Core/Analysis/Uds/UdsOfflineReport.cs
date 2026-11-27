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

    /// <summary>
    /// 会话时间线：这段日志里 ECU 换过几次会话、最后确认停在哪个。
    /// 空列表 = 未执行 diagnose 阶段（部分报告）或日志里没有任何事务。
    /// </summary>
    public IReadOnlyList<UdsSessionInterval> SessionTimeline { get; init; }
        = Array.Empty<UdsSessionInterval>();

    /// <summary>刷写流程画像；<c>null</c> 或 <c>LooksLikeFlash == false</c> 表示不是刷写日志。</summary>
    public UdsFlashFlow? FlashFlow { get; init; }

    /// <summary>可复核的观测事实。结论通过 <see cref="UdsFinding.ObservationIndexes"/> 引用它。</summary>
    public IReadOnlyList<UdsObservation> Observations { get; init; }
        = Array.Empty<UdsObservation>();

    /// <summary>
    /// 结论列表。<b>可能有多条互不相关</b>——刻意不做单一根因链，
    /// 详见 2026-11-26 设计文档 §2.3。
    /// </summary>
    public IReadOnlyList<UdsFinding> Findings { get; init; }
        = Array.Empty<UdsFinding>();

    /// <summary>
    /// 结论之间的关系。P0 阶段恒为空数组：关联需要会话/安全状态机（P1），
    /// 在没有状态机时硬猜因果关系正是设计文档要避免的错误。
    /// </summary>
    public IReadOnlyList<UdsFindingLink> FindingLinks { get; init; }
        = Array.Empty<UdsFindingLink>();

    /// <summary>回显校验失败明细。此前只有总数，没有具体原因。</summary>
    public IReadOnlyList<UdsResponseValidationFailure> ValidationFailures { get; init; }
        = Array.Empty<UdsResponseValidationFailure>();

    /// <summary>
    /// 重复最终响应（同一请求的第二个最终响应）。
    /// spec §6.5 / §12 要求「单列计数和证据」——只给计数时用户不知道
    /// 重复的是哪一条，也就无法判断是不是并行诊断源。
    /// </summary>
    public IReadOnlyList<UdsOfflinePdu> DuplicateFinalResponsePdus { get; init; }
        = Array.Empty<UdsOfflinePdu>();

    /// <summary>
    /// 非诊断报文（背景流量）的画像。
    /// <para>
    /// 替代原来的「Top-20 个 ID × 帧数」：那 20 个 ID 通常是其它 ECU 的正常
    /// 周期广播，列出来说明不了任何事，反而把真正的问题帧淹没。
    /// </para>
    /// </summary>
    public UdsBusTrafficProfile? BusTraffic { get; init; }
}
