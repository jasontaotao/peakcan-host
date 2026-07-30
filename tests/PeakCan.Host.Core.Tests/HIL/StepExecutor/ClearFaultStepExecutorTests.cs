using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.Fakes;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;

public class ClearFaultStepExecutorTests
{
    [Fact]
    public async Task ClearFault_clears_by_FaultId()
    {
        var fakeCtx = new FakeIFaultInjectionContext();
        var executor = new ClearFaultStepExecutor();
        var step = TestCaseStep.Create(new ClearFaultStep(FaultId: "fault1"));

        var result = await executor.ExecuteAsync(step, fakeCtx, CancellationToken.None);

        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Single(fakeCtx.ClearedFaultIds);
        Assert.Equal("fault1", fakeCtx.ClearedFaultIds[0]);
    }

    [Fact]
    public async Task ClearFault_clears_all_when_FaultId_null()
    {
        var fakeCtx = new FakeIFaultInjectionContext();
        var executor = new ClearFaultStepExecutor();
        var step = TestCaseStep.Create(new ClearFaultStep(FaultId: null));

        var result = await executor.ExecuteAsync(step, fakeCtx, CancellationToken.None);

        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal(1, fakeCtx.ClearAllCallCount);
    }
}
