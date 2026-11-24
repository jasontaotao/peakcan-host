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

/// <summary>
/// 未建模条款：显式可见，AI 不得据此给结论（spec §7.1.1）。
/// <para><see cref="Clause"/> 是条款号（如 §5.5），不是规则 ID；规则身份见 <see cref="RuleId"/>。</para>
/// </summary>
public sealed record NmUnmodeledRule(
    string Clause,
    string Reason,
    string? RequirementId = null,
    string? SourceRef = null)
{
    /// <summary>未实现的规则 ID；Clause 是条款号，两者不是一回事。</summary>
    public string? RuleId { get; init; }

    /// <summary>结构化原因码，见 spec §9.4 / <see cref="NmUnmodeledCodes"/>。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>
    /// 本规则覆盖的完整条款列表（来自 rule.Clauses）。
    /// <see cref="Clause"/> 只是 ordinal-first 的兼容字段：多条款规则
    /// （NM_STOP_SENDING → NM_09/NM_12）只读 Clause 会丢条款。
    /// </summary>
    public IReadOnlyList<string> Clauses { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 时间条上的一个活动段（spec §7.5）。
/// <para>
/// <see cref="Evidence"/> 与两个 bool <b>正交</b>，不得互相替代：
/// 证据等级说「这段凭什么画出来」，两个 bool 说「段的起止边界是怎么确定的」。
/// 把证据塞进 <see cref="Kind"/>（如 <c>repeat_measured</c>）会让枚举翻倍，
/// 并打断现有的笔刷/文案映射（spec §7.5.2）。
/// </para>
/// </summary>
/// <param name="Evidence">
/// 与 <see cref="NmEvent.EvidenceKind"/> 同一词表：<c>measured</c> | <c>inferred</c> | <c>boundary</c>。
/// <para>
/// <c>measured</c> = 有实测依据（NMSts 位值、观测到的快发段）；
/// <c>inferred</c> = 时间推断（T_REPEAT_MESSAGE 兜底窗、active/released）；
/// <c>boundary</c> = 段本身只因窗口/报告边界才存在，内部没有任何可用证据。
/// </para>
/// <para>
/// 默认 <c>inferred</c>：M1 的全部现有段都来自时间推断，语义正确，
/// 老构造点不写该参数时自动得到正确的等级。
/// </para>
/// </param>
/// <param name="IsWindowTruncated">
/// 段的终点恰好等于窗口/报告边界，即这一段是<b>被边界切断</b>的。
/// 严格定义：只有真正被切断才为 true，不是「状态可能在窗口外延续」。
/// </param>
/// <param name="StateBoundaryKnown">
/// 段内是否观测到状态跃迁（NMSts 变化）。false = 窗口内只有单一状态，
/// 状态边界不可判明——<b>不代表被截断</b>。
/// </param>
public sealed record NmSegment(
    double Start,
    double End,
    string Kind,
    string Evidence = "inferred",
    bool IsWindowTruncated = false,
    bool StateBoundaryKnown = false);

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
    // 已弃用（spec §4.0）：多条款时单值会造假归因。新代码读 Clauses。
    string? RequirementId = null,
    string? SourceRef = null)
{
    /// <summary>本 finding 覆盖的条款号（来自 rule.Clauses）。</summary>
    public IReadOnlyList<string> Clauses { get; init; } = Array.Empty<string>();
}

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
    IReadOnlyList<NmUnmodeledRule>? UnmodeledRules = null,
    // spec §9.2：规则求值前的数据体检结果；null 表示未执行体检。
    IReadOnlyList<NmDataQualityCheck>? DataQualityChecks = null)
{
    /// <summary>
    /// 逐规则求值状态（spec §7.1.1）。空列表 = 规则引擎没跑（未选中企标 profile）。
    /// <para>
    /// <b>为什么报告必须有它</b>：合规规则**零输出**（<c>BitRule</c> / <c>LayoutCheck</c>
    /// 等在合法场景 <c>return true</c> 就结束，不产 finding 也不产 observation）。
    /// 只看 <see cref="Findings"/> / <see cref="Observations"/> 时，
    /// 「跑了且合规」与「压根没跑」在报告里长得一模一样——用户无法判断
    /// 空着的那条是「没问题」还是「没检查」。
    /// </para>
    /// <para>
    /// 非可空：空列表已能表达「没跑」，再加一层 null 只会让消费方多写一个
    /// 永远走不到的兜底分支。
    /// </para>
    /// </summary>
    public IReadOnlyList<NmRuleEvaluation> RuleEvaluations { get; init; }
        = Array.Empty<NmRuleEvaluation>();

    /// <summary>
    /// 条款覆盖统计（spec §5.5）。由 <see cref="RuleEvaluations"/> 与规则的
    /// <c>Clauses</c> 聚合而来，回答「吉利规范的每一条，这次到底验没验」。
    /// <para>
    /// 空列表 = 没有规则声明任何条款（如未选企标 profile）。
    /// </para>
    /// </summary>
    public IReadOnlyList<NmClauseCoverageRow> ClauseCoverage { get; init; }
        = Array.Empty<NmClauseCoverageRow>();
}

/// <summary>数据体检的三态结果（spec §9.2）。</summary>
public enum NmDataQualityStatus
{
    Passed = 0,
    Failed = 1,
    // 不是 Failed：数据没问题叫「通过」，数据不够叫「没查成」，结论强度不同。
    NotChecked = 2,
}

/// <summary>受影响的时间区间。用具名 record 而非 ValueTuple：后者 JSON 序列化会丢元素名。</summary>
public sealed record NmTimeRange(double Start, double End);

/// <summary>单项体检结果（spec §9.2）。</summary>
public sealed record NmDataQualityCheck(
    string Code,
    NmDataQualityStatus Status,
    string Detail,
    IReadOnlyList<NmTimeRange> Ranges,
    IReadOnlyList<string> AffectedCanIds);
