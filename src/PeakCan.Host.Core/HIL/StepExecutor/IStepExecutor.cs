using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Step executor strategy interface. Each StepKind maps to one implementation.
/// Executors never throw on assertion failure — they return StepResult.Fail.
/// Cancellation (OperationCanceledException) propagates normally.
/// </summary>
public interface IStepExecutor
{
    TestCaseStepKind Kind { get; }

    Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct);
}
