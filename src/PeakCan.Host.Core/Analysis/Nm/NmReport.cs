namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmWindow(double Start, double End, string WindowRef);

public sealed record NmCycleStats(
    double? MedianMs,
    double? P95Ms,
    double? MinMs,
    double? MaxMs,
    double? PhaseMs,
    int DeviationCount,
    int IntervalCount = 0,
    int? ExpectedMs = null,
    int? ToleranceMs = null,
    int ConsecutiveDeviationCount = 0,
    // 有效间隔 < 5 时为 true：中位数/相位/偏差均为空，消费方不应据此下结论。
    bool InsufficientSamples = false);

/// <summary>
/// 静默判定（spec §7.1.2）。bool 装不下「全网静默」与「单节点静默」的区别，
/// 前者是原因未知的事实，后者才是可疑。
/// </summary>
public enum NmSilenceVerdict
{
    /// <summary>窗口末仍在发送。</summary>
    NotSilent = 0,

    /// <summary>该报文静默且其它 NM 报文仍在发送 → 可疑。</summary>
    SilentWithOtherNmActive,

    /// <summary>全部 NM 静默 → 原因未知（释放/下电/记录停止/bus sleep 无法区分）。</summary>
    AllNmSilent,

    /// <summary>窗口截断 / 共享 ID 无法拆分 → 不可判明。</summary>
    IndeterminateAtBoundary,

    /// <summary>
    /// 停发前最后一帧已处于 Ready Sleep State（NMSts=1），其它 NM 仍在发送：
    /// 这是 CanNm 按 NM_09 完成的合规释放——节点确实不再发送，但不是「可疑的单节点静默」。
    /// 与 <see cref="SilentWithOtherNmActive"/> 分开，避免 AI 把合规释放读成节点异常。
    /// 新增在末尾，不改动既有成员的数值（可能有外部序列化依赖）。
    /// </summary>
    ReleasedInReadySleep,
}

/// <summary>观测事实：可量化、可复核，但不声称违规（spec §7.1.1）。</summary>
public sealed record NmObservation(
    string Kind,
    string? Node,
    string? CanIdHex,
    double Start,
    double End,
    string Detail,
    string EvidenceKind,
    bool IsInference = false,
    string? RuleId = null);

/// <summary>未建模条款：显式可见，AI 不得据此给结论（spec §7.1.1）。</summary>
public sealed record NmUnmodeledRule(
    string Clause,
    string Reason,
    string? RequirementId = null,
    string? SourceRef = null);

public sealed record NmSegment(double Start, double End, string Kind);

public sealed record NmEpisode(
    int Index,
    double Start,
    double End,
    string Kind,
    string? WakeupNid,
    IReadOnlyList<string> Participants,
    bool BoundaryStart,
    bool BoundaryEnd);

public sealed record NmMessageReport(
    string CanIdHex,
    bool IsExtended,
    string? Name,
    string? Sender,
    int FrameCount,
    double FirstSeen,
    double LastSeen,
    NmCycleStats Cycle,
    IReadOnlyList<NmSegment> Activity,
    NmSilenceVerdict SilenceVerdict = NmSilenceVerdict.NotSilent)
{
    /// <summary>
    /// 兼容属性：语义改为「单节点静默」（spec §7.1.2）。由 <see cref="SilenceVerdict"/> 派生，
    /// 不可独立赋值——避免调用方传 true 而 verdict 是 AllNmSilent 的自相矛盾状态。
    /// </summary>
    public bool SilentBeyondTimeout => SilenceVerdict == NmSilenceVerdict.SilentWithOtherNmActive;
}

public sealed record NmNodeReport(
    string? Nid,
    string? CanIdHex,
    byte? StationAddress,
    double FirstSeen,
    double LastSeen,
    int FrameCount,
    NmCycleStats Cycle,
    IReadOnlyList<NmSegment> Activity,
    bool LeftEarly,
    bool SilentBeyondTimeout);

/// <summary>EvidenceKind: measured | inferred | boundary。</summary>
public sealed record NmEvent(
    double T,
    string Type,
    string? Node,
    string? CanIdHex,
    string Detail,
    int EpisodeIndex,
    string EvidenceKind,
    bool IsExtended = false);

public sealed record NmFinding(
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<double> EvidenceTimes,
    bool IsInference,
    string? SuggestedNextCheck,
    string? RuleId = null,
    string? RequirementId = null,
    string? SourceRef = null);

public sealed record NmTruncation(string Section, int Returned, int Total);

public sealed record NmInputMissing(
    NmCapability Capability,
    string InputKey,
    string Why,
    string? Suggestion = null,
    string? SuggestionSource = null);

public sealed record NmCapabilityState(
    NmCapability Capability,
    bool Available,
    IReadOnlyList<NmInputMissing> Missing);

public sealed record NmDirectionCounts(int Transmit, int Receive, int Unknown);

public sealed record NmReport(
    NmWindow Window,
    string SourceId,
    ushort? Channel,
    IReadOnlyList<NmEpisode> Episodes,
    IReadOnlyList<NmMessageReport> Messages,
    IReadOnlyList<NmNodeReport> Nodes,
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<NmCapabilityState> Capabilities,
    IReadOnlyList<NmInputMissing> MissingInputs,
    IReadOnlyList<NmTruncation> Truncations,
    string? StandardId = null,
    string? ProfileHash = null,
    string? EngineVersion = null,
    // 选择器丢弃/过滤计数：让"为什么少了很多帧"可复核，而不是算完即丢。
    NmSelectionDiagnostics? Selection = null,
    NmDirectionCounts? Direction = null,
    // 取消/超时导致分析未跑完：报告只包含已完成阶段的数据（spec §9.4）。
    bool IsPartial = false,
    // spec §7.1.1：事实与违规必须分开，否则必然误报。
    IReadOnlyList<NmObservation>? Observations = null,
    IReadOnlyList<NmUnmodeledRule>? UnmodeledRules = null);
