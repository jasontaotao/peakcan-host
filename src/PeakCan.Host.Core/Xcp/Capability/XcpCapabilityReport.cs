namespace PeakCan.Host.Core.Xcp.Capability;

/// <summary>
/// 能力对账发现项严重度（S2-T7 分级标准，钉死、不留运行时可调档位）：
/// <see cref="Warning"/> = 不直接破坏采集正确性的事实（A2L 内部矛盾提醒、
/// 未知块、Missing 留痕、CAN ID 待核实等）；<see cref="Reject"/> = 硬约束字段
/// 不一致或无法核对——拒绝启动（宁可不采，不许静默错采，spec §4）。
/// </summary>
public enum XcpCapabilitySeverity
{
    /// <summary>告警：采集仍可启动，事实清单必须浮出给人。</summary>
    Warning,

    /// <summary>拒绝：硬约束不匹配 / 无法核对，禁止启动采集。</summary>
    Reject,
}

/// <summary>一条对账发现项。Code 是稳定机读标识（见 XcpCapabilityReconciler 各规则注释）。</summary>
public sealed record XcpCapabilityFinding(
    XcpCapabilitySeverity Severity,
    string Code,
    string Message);

/// <summary>
/// 能力对账输出（spec §3 Capability）：告警清单 + 是否拒绝启动。
/// <see cref="RejectedStart"/> = true 当且仅当存在至少一条 <see cref="XcpCapabilitySeverity.Reject"/>。
/// </summary>
public sealed record XcpCapabilityReport(
    bool RejectedStart,
    IReadOnlyList<XcpCapabilityFinding> Findings)
{
    /// <summary>仅告警的发现项（拒绝项不重复出现）。</summary>
    public IEnumerable<XcpCapabilityFinding> Warnings =>
        Findings.Where(f => f.Severity == XcpCapabilitySeverity.Warning);
}
