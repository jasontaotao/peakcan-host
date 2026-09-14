namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// Fail-loud bridge for the per-case SecOC stats reset (spec Rev7). The engine
/// invokes <see cref="PeakCan.Host.Core.HIL.Contracts.IPerCaseReset.ResetPerCase"/>
/// on the assertion context at each case start; contexts delegate here with their
/// wired <c>ISecOcStats</c>. A stats implementation that lacks the reset
/// capability must fail loudly rather than silently no-op (which would
/// re-introduce the attack-suite false-pass).
/// </summary>
internal static class SecOcStatsReset
{
    public static void ResetPerCase(global::PeakCan.Host.Core.HIL.Contracts.ISecOcStats? stats)
    {
        switch (stats)
        {
            case null:
                return;
            case global::PeakCan.Host.Core.HIL.Contracts.IPerCaseReset resettable:
                resettable.ResetPerCase();
                return;
            default:
                throw new InvalidOperationException(
                    $"SecOC stats implementation '{stats.GetType().Name}' does not implement " +
                    "PeakCan.Host.Core.HIL.Contracts.IPerCaseReset; per-case reset would be a silent " +
                    "no-op and re-introduce the attack-suite false-pass.");
        }
    }
}
