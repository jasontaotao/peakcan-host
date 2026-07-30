using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertDtc steps. Checks if a specific DTC is present/absent.
/// </summary>
internal sealed class AssertDtcStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertDtcStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertDtc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertDtcStep)step.Parameters;
        try
        {
            var dtcs = await _uds.ReadDtcInformation(0xFF, ct);

            if (p.DtcCode is null)
            {
                // Any DTC present?
                bool anyActive = dtcs.Any(d => (d.Status & 0x01) != 0 || (d.Status & 0x04) != 0);
                return p.ExpectPresent
                    ? (anyActive
                        ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed, "at least one DTC present", null, null, 0)
                        : new StepResult(0, step.Kind, step.Label, StepStatus.Failed, "no DTC present", "0", ">=1", 0))
                    : (anyActive
                        ? new StepResult(0, step.Kind, step.Label, StepStatus.Failed, "unexpected DTC present", ">=1", "0", 0)
                        : new StepResult(0, step.Kind, step.Label, StepStatus.Passed, "no DTC present", "0", "0", 0));
            }

            // 用 Any 而非 FirstOrDefault — 避免 default(DtcInfo).Code == 0 误匹配 DTC 0x0000
            bool isActive = dtcs.Any(d => d.Code == p.DtcCode.Value
                && ((d.Status & 0x01) != 0 || (d.Status & 0x04) != 0));

            return p.ExpectPresent
                ? (isActive
                    ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed, $"DTC 0x{p.DtcCode:X4} present", null, null, 0)
                    : new StepResult(0, step.Kind, step.Label, StepStatus.Failed, $"DTC 0x{p.DtcCode:X4} not found", "absent", "present", 0))
                : (isActive
                    ? new StepResult(0, step.Kind, step.Label, StepStatus.Failed, $"DTC 0x{p.DtcCode:X4} unexpectedly present", "present", "absent", 0)
                    : new StepResult(0, step.Kind, step.Label, StepStatus.Passed, $"DTC 0x{p.DtcCode:X4} absent", null, null, 0));
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed, $"UDS error: {ex.Message}", null, null, 0);
        }
    }
}
