namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Result of a single step execution.
/// </summary>
public sealed record StepResult(
    int StepIndex,
    TestCaseStepKind Kind,
    string? Label,
    StepStatus Status,
    string? Message,
    string? ActualValue,
    string? ExpectedValue,
    int ElapsedMs,
    IReadOnlyList<CanFrame>? FramesAroundFailure = null)
{
    public bool Passed => Status == StepStatus.Passed;
}
