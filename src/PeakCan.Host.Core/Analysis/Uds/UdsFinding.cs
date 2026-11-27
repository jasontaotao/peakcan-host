namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>结论的严重程度。只影响展示排序与图标，不影响事实本身。</summary>
public enum UdsFindingSeverity
{
    Info,
    Warn,
    Error,
}

/// <summary>两条 finding 之间的关系。**默认是「不确定」而不是「有因果」。**</summary>
public enum UdsFindingRelation
{
    /// <summary>
    /// 证据表明是同一前置问题（例如同一安全等级始终未解锁，后续多次 0x33）。
    /// 必须有反证检查：两者之间不能夹着成功的事务。
    /// </summary>
    LikelySharedCause,

    /// <summary>时间相邻，但日志里没有构成因果的证据。**这是默认值。**</summary>
    TemporallyAdjacent,

    /// <summary>已确认无关（例如两者之间夹了成功的同类事务）。</summary>
    Independent,
}

/// <summary>
/// 一条可量化、可复核的观测事实。语义与 NM 分析的 <c>NmObservation</c> 一致：
/// 事实与推断分开，事实不声称违规。
/// </summary>
/// <param name="Kind">
/// 观测类型。<b>当前实际产出三种</b>：<c>negative_response</c> /
/// <c>unclosed_request</c> / <c>flash_incomplete</c>。
/// <para>
/// 设计文档 §4.2 曾列出 <c>session_switch</c> / <c>security_attempt</c> /
/// <c>transfer_block</c> / <c>p2_exceeded</c> 等更多类型，那些属于 P1 的状态机
/// 规划，本版没有对应规则——不要照着文档假设它们会出现在报告里。
/// </para>
/// </param>
/// <param name="IsInference">
/// true = 从「没有观察到 X」推断出来的，而非直接观测到的。
/// 例如「该安全等级未解锁」是从未见到 <c>0x27 02</c> 正响应推断的。
/// </param>
public sealed record UdsObservation(
    string Kind,
    int? TransactionIndex,
    int FirstEvidenceIndex,
    int LastEvidenceIndex,
    double Timestamp,
    UdsOperationFacts? Facts,
    string Detail,
    bool IsInference = false);

/// <summary>
/// 一条结论。
/// <para>
/// <b>文案纪律（验收标准第 8 条）：</b><see cref="Message"/> 只能陈述日志事实；
/// 倾向性判断放 <see cref="Suspect"/>，UI 固定渲染成「优先排查」；
/// 不写「根因是」「一定是」。
/// </para>
/// <para>
/// <b>为什么保留 <see cref="Unknowns"/>：</b>用户最容易误读的状态是「没看到」，
/// 他会读成「ECU 没回」。必须显式列出「仅凭本日志无法判定什么」。
/// </para>
/// </summary>
public sealed record UdsFinding(
    string Code,
    UdsFindingSeverity Severity,
    string Message,
    UdsObservedAction? ObservedAction,
    UdsSuspect Suspect,
    IReadOnlyList<int> ObservationIndexes,
    string? SuggestedNextCheck,
    bool IsInference,
    IReadOnlyList<string> Unknowns);

/// <summary>
/// 两条 finding 之间的关系声明。
/// <para>
/// <b>必须有 <see cref="Basis"/>：</b>关系本身也是结论，用户要能复核
/// 「你凭什么说这两个是一回事」。没有依据的关联等于编造。
/// </para>
/// </summary>
public sealed record UdsFindingLink(
    string FromCode,
    string ToCode,
    UdsFindingRelation Relation,
    string Basis);
