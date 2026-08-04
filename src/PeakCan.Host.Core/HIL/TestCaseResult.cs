namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Result of a single test case execution.
/// Passed = no step has Status == Failed (Comment/Skipped don't cause failure).
/// TotalSteps excludes Comment steps (executable steps only).
/// Invariant: PassedSteps + FailedSteps + SkippedSteps == TotalSteps.
/// </summary>
public sealed record TestCaseResult(
    string TestCaseId,
    string TestCaseName,
    bool Passed,
    string? FailureReason,
    int ElapsedMs,
    int TotalSteps,
    int PassedSteps,
    int FailedSteps,
    int SkippedSteps,
    int CommentSteps,
    IReadOnlyList<StepResult> StepResults);
