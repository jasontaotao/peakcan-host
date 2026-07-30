using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.Fakes;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;



public class InjectFaultStepDirectionTests
{
    private static InjectFaultStep CreateStep(FaultDirection direction)
        => new(
            new CanId(0x100, FrameFormat.Standard),
            FaultType.Drop,
            1.0,
            0,
            null,
            (byte)0xFF,
            null,
            direction);

    private static TestCaseStep CreateStepWithDirection(FaultDirection direction)
        => TestCaseStep.Create(CreateStep(direction));

    [Fact]
    public async Task Executor_SendDirection_CallsAddFault()
    {
        var ctx = new FakeIFaultInjectionContext();
        var executor = new InjectFaultStepExecutor();
        var step = CreateStepWithDirection(FaultDirection.Send);

        await executor.ExecuteAsync(step, ctx, default);

        Assert.Single(ctx.AddedFaults);
        Assert.Empty(ctx.AddedReceiveFaults);
    }

    [Fact]
    public async Task Executor_ReceiveDirection_CallsAddReceiveFault()
    {
        var ctx = new FakeIFaultInjectionContext();
        var executor = new InjectFaultStepExecutor();
        var step = CreateStepWithDirection(FaultDirection.Receive);

        await executor.ExecuteAsync(step, ctx, default);

        Assert.Empty(ctx.AddedFaults);
        Assert.Single(ctx.AddedReceiveFaults);
    }

    [Fact]
    public async Task Executor_BothDirection_CallsBoth()
    {
        var ctx = new FakeIFaultInjectionContext();
        var executor = new InjectFaultStepExecutor();
        var step = CreateStepWithDirection(FaultDirection.Both);

        await executor.ExecuteAsync(step, ctx, default);

        Assert.Single(ctx.AddedFaults);
        Assert.Single(ctx.AddedReceiveFaults);
    }

    [Fact]
    public async Task Executor_SendDirection_BackwardCompatible_WhenDirectionOmitted()
    {
        // Create a step without specifying direction (defaults to Send)
        var param = new InjectFaultStep(
            new CanId(0x100, FrameFormat.Standard),
            FaultType.Drop,
            1.0, 0, null, (byte)0xFF, null);

        var ctx = new FakeIFaultInjectionContext();
        var executor = new InjectFaultStepExecutor();
        var step = TestCaseStep.Create(param);

        await executor.ExecuteAsync(step, ctx, default);

        // Default direction is Send -> AddFault called, AddReceiveFault not called
        Assert.Single(ctx.AddedFaults);
        Assert.Empty(ctx.AddedReceiveFaults);
    }
}
