namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>Per-canId SecOC verdict bucket (spec §5-D6.2). Reason is a plain
/// string so Core keeps no dependency on the security library.</summary>
public sealed record SecOcVerdictBucket(long Accepted, long Rejected, string? LastReason);

/// <summary>Read-side SecOC verdict statistics (written by the SecOcChannel RX path).</summary>
public interface ISecOcStats
{
    bool TryGet(uint canId, out SecOcVerdictBucket bucket);
}

/// <summary>
/// Capability interface (spec §5-D1 capability resolution): implemented by
/// assertion contexts that can supply SecOC statistics; the expression scope
/// factory discovers it via <c>ctx is ISecOcStatsSource</c>.
/// <para>
/// 项 2（2026-09-17）：多通道逐通道路由——实现方可选提供 <see cref="SecOcStatsFor"/>；
/// 不实现（旧实现）时 DIM 默认回落 <see cref="SecOcStats"/>（单通道向后兼容）。
/// </para>
/// </summary>
public interface ISecOcStatsSource
{
    ISecOcStats? SecOcStats { get; }

    /// <summary>按通道名取 SecOC 统计（null/空 = 默认通道）。DIM 默认回落 <see cref="SecOcStats"/>。</summary>
    ISecOcStats? SecOcStatsFor(string? channelName) => SecOcStats;
}
