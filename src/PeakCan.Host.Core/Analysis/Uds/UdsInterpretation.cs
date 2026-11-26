namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Read-only explanation of one transaction. <see cref="DecodedValue"/> is null
/// whenever a degrade reason is set — the report keeps the raw payload and shows
/// the reason instead of guessing.
/// </summary>
public sealed record UdsInterpretation(
    string ServiceDisplay,
    string? DidName,
    string? RoutineName,
    string? DecodedValue,
    UdsDegradeReason? DegradeReason,
    string? DegradeDetail)
{
    /// <summary>
    /// 请求 SID 不在已知服务表内（D17）：事务照常建立并标记，
    /// 不能因为服务表不全就把真实 OEM 服务丢掉。
    /// </summary>
    public bool IsUnknownService { get; init; }
}
