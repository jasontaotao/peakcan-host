using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;

namespace PeakCan.Host.Infrastructure.HIL.StepExecutor;

/// <summary>
/// ModifyBackgroundFrame 步骤执行器：替换运行中后台帧的数据。
/// 通过 DI 注入 BackgroundFrameSender。
/// </summary>
public sealed class ModifyBackgroundFrameStepExecutor : IStepExecutor
{
    private readonly BackgroundFrameSender _sender;

    public ModifyBackgroundFrameStepExecutor(BackgroundFrameSender sender)
    {
        _sender = sender;
    }

    public TestCaseStepKind Kind => TestCaseStepKind.ModifyBackgroundFrame;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ModifyBackgroundFrameStep)step.Parameters;
        _sender.UpdateFrameData(p.Id, p.Data);
        return Task.FromResult(new StepResult(
            StepIndex: 0,
            Kind: step.Kind,
            Label: step.Label,
            Status: StepStatus.Passed,
            Message: $"Modified background frame 0x{p.Id.Raw:X} data to {Convert.ToHexString(p.Data)}",
            ActualValue: null,
            ExpectedValue: null,
            ElapsedMs: 0));
    }
}
