namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Execution status of a single test step.
/// </summary>
public enum StepStatus
{
    /// <summary>Step executed successfully.</summary>
    Passed,

    /// <summary>Step executed and failed assertion or threw exception.</summary>
    Failed,

    /// <summary>Step not executed due to previous failure (StopCaseOnFailure).</summary>
    Skipped,

    /// <summary>Documentation step, not executed. Does NOT affect case pass/fail.</summary>
    Comment,
}
