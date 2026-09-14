namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Per-case reset capability (spec Rev7). The engine invokes this once at each
/// case start (before case setup), mirroring the per-case variable clear, so
/// cumulative subsystem state cannot leak across cases. Implemented by
/// assertion contexts that own such state (e.g. SecOC verdict statistics):
/// an earlier case's rejection must not satisfy the next case's
/// <c>secocRejected(id)</c> assertion.
/// </summary>
public interface IPerCaseReset
{
    /// <summary>Clear all per-case cumulative state. Called once per case, before setup.</summary>
    void ResetPerCase();
}
