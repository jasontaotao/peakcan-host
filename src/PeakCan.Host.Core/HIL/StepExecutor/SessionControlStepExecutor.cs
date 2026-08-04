using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes SessionControl steps. Switches the ECU diagnostic session via UDS;
/// the negotiated P2/P2* timings are applied inside UdsClient.
/// </summary>
internal sealed class SessionControlStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public SessionControlStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.SessionControl;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (SessionControlStep)step.Parameters;
        try
        {
            var response = await _uds.DiagnosticSessionControlAsync(p.Session, ct);
            if (ctx is IStepVariableStore store)
                store.Variables["session"] = new[] { p.Session };   // byte[] 统一，供 AssertDidValue 断言
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Session switched to 0x{p.Session:X2} (P2={response.P2}ms, P2*={response.P2Star}ms)", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SessionControl 0x{p.Session:X2} failed: {ex.Message}", null, null, 0);
        }
    }
}
