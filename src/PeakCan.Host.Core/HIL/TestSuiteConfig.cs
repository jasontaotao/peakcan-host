namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Configuration for test suite execution.
/// </summary>
/// <param name="FailurePolicy">
/// How to propagate failures within and across cases.
/// </param>
/// <param name="ContinueAfterSetupFailure">
/// When true, suite-level setup failure still allows cases to execute.
/// When false, all cases are skipped after setup failure.
/// </param>
public sealed record TestSuiteConfig(
    FailurePolicy FailurePolicy = FailurePolicy.ContinueAll,
    bool ContinueAfterSetupFailure = true);

/// <summary>
/// Failure propagation policy.
/// </summary>
public enum FailurePolicy
{
    /// <summary>Continue all steps and cases regardless of failures.</summary>
    ContinueAll,

    /// <summary>Skip remaining steps in case after first failure, but continue suite.</summary>
    StopCaseOnFailure,

    /// <summary>Stop entire suite after first case failure.</summary>
    StopSuiteOnFailure,
}
