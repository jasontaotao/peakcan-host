namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Placeholder for SendSequence steps. Not supported in Sprint 1.
/// </summary>
internal sealed class SendSequenceStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.SendSequence;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        throw new NotSupportedException("SendSequence not supported in Sprint 1");
    }
}
