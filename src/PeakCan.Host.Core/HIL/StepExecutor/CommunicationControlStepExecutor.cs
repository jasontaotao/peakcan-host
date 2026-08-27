using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes CommunicationControl (0x28) steps — physical addressing via
/// SendRequestAsync (expects positive response).
/// ⚠️ 勿用 UdsClient.CommunicationControlAsync：它是 functional fire-and-forget。
/// </summary>
internal sealed class CommunicationControlStepExecutor : IStepExecutor
{
    private readonly IUdsSessionResolver _resolver;

    public CommunicationControlStepExecutor(IUdsSessionResolver resolver) => _resolver = resolver;
    public TestCaseStepKind Kind => TestCaseStepKind.CommunicationControl;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (CommunicationControlStep)step.Parameters;
        var session = _resolver.Resolve(p.TargetChannel);
        try
        {
            await session.SendRequestAsync(0x28, new[] { p.ControlType }, ct);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"CommunicationControl 0x{p.ControlType:X2} acknowledged", null, null, 0, Channel: p.TargetChannel);
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"CommunicationControl 0x{p.ControlType:X2} failed: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
    }
}
