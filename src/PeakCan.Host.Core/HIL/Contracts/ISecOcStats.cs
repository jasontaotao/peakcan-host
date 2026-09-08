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
/// </summary>
public interface ISecOcStatsSource
{
    ISecOcStats? SecOcStats { get; }
}
