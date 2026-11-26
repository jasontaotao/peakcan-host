namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>How a request's exchange ended. <see cref="None"/> = still open at log end.</summary>
public enum UdsFinalKind
{
    Positive,
    Negative,
    None,
}

/// <summary>
/// One request paired with the responses seen for it. <see cref="IsAmbiguous"/>
/// is set when two same-SID requests were open at once or a duplicate final
/// response arrived — the pairing is then a best guess, and the report says so
/// instead of presenting it as fact.
/// </summary>
public sealed record UdsOfflineTransaction(
    UdsOfflinePdu Request,
    IReadOnlyList<UdsOfflinePdu> Responses,
    UdsFinalKind FinalKind,
    int PendingCount,
    byte? FinalNrc,
    bool IsOrphan,
    bool IsAmbiguous,
    bool HasDuplicateFinalResponse,
    IReadOnlyList<string> Unknowns)
{
    public int EvidenceIndex => Request.FirstEvidenceIndex;
    public double FirstTimestamp => Request.FirstTimestamp;

    /// <summary>请求结束到首个响应的毫秒数；没有响应时为 null。</summary>
    public double? FirstResponseMs { get; init; }

    /// <summary>请求开始到最终响应的毫秒数；事务未闭合时为 null（不能拿“最后一个响应”冒充总耗时）。</summary>
    public double? TotalMs { get; init; }

    /// <summary>P2/P2* 的来源事务（例如 <c>ECU:0x50</c>）；无会话控制响应时为 <c>Unknown</c>。</summary>
    public string TimingSource { get; init; } = "Unknown";

    /// <summary>服务解释结果；由 <see cref="UdsOfflineAnalyzer"/> 在报告组装阶段填充。</summary>
    public UdsInterpretation? Interpretation { get; init; }

    /// <summary>
    /// 首个响应耗时超过 ECU 声明的 P2（只做标记，不剔除事务）。没有会话控制响应时恒为 false。
    /// </summary>
    public bool ExceedsP2 { get; init; }
}

/// <summary>
/// Pairing output. The three failure lists are the transport-valid PDUs that
/// could not be paired or did not pass the echo checks; the report turns them
/// into noise rows with explicit reasons.
/// </summary>
public sealed record UdsTransactionBuildResult(
    IReadOnlyList<UdsOfflineTransaction> Transactions,
    IReadOnlyList<UdsOfflinePdu> OrphanResponses,
    IReadOnlyList<UdsOfflinePdu> ValidationFailures,
    IReadOnlyList<UdsOfflinePdu> DuplicateFinalResponses)
{
    public IReadOnlyList<string> FailedChecks { get; init; } = Array.Empty<string>();
}
