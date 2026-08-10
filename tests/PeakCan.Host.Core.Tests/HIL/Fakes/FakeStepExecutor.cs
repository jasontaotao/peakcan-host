using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.Fakes;

/// <summary>
/// Configurable fake IStepExecutor for testing TestSuiteEngine.
/// </summary>
internal sealed class FakeStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind { get; }
    public int ExecuteCallCount { get; private set; }
    public StepResult Result { get; set; } = new(0, TestCaseStepKind.Delay, null, StepStatus.Passed, "fake", null, null, 0);
    public Exception? ExceptionToThrow { get; set; }

    public FakeStepExecutor(TestCaseStepKind kind) => Kind = kind;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        ExecuteCallCount++;
        if (ExceptionToThrow is not null)
            return Task.FromException<StepResult>(ExceptionToThrow);
        return Task.FromResult(Result with { Kind = step.Kind, StepIndex = 0, ElapsedMs = 0 });
    }
}
