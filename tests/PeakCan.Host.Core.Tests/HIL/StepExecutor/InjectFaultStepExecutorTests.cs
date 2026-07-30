using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.Fakes;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;

public class InjectFaultStepExecutorTests
{
    [Fact]
    public async Task InjectFault_adds_rule_to_context()
    {
        var fakeCtx = new FakeIFaultInjectionContext();
        var executor = new InjectFaultStepExecutor();
        var step = TestCaseStep.Create(new InjectFaultStep(
            CanId: new CanId(0x123, FrameFormat.Standard),
            FaultType: FaultType.Drop,
            Probability: 1.0,
            DelayMs: 0,
            CorruptByteIndices: null,
            CorruptXorMask: 0xFF,
            FaultId: null));

        var result = await executor.ExecuteAsync(step, fakeCtx, CancellationToken.None);

        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Single(fakeCtx.AddedFaults);
        Assert.Equal(FaultType.Drop, fakeCtx.AddedFaults[0].Type);
    }

    [Fact]
    public async Task InjectFault_fails_when_context_not_IFaultInjectionContext()
    {
        var fakeCtx = new FakeAssertionContext();
        var executor = new InjectFaultStepExecutor();
        var step = TestCaseStep.Create(new InjectFaultStep(
            CanId: new CanId(0x123, FrameFormat.Standard),
            FaultType: FaultType.Drop,
            Probability: 1.0,
            DelayMs: 0,
            CorruptByteIndices: null,
            CorruptXorMask: 0xFF,
            FaultId: null));

        var result = await executor.ExecuteAsync(step, fakeCtx, CancellationToken.None);

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("does not support fault injection", result.Message);
    }

    [Fact]
    public async Task InjectFault_tags_FaultId()
    {
        var fakeCtx = new FakeIFaultInjectionContext();
        var executor = new InjectFaultStepExecutor();
        var step = TestCaseStep.Create(new InjectFaultStep(
            CanId: new CanId(0, FrameFormat.Standard),
            FaultType: FaultType.Delay,
            Probability: 0,
            DelayMs: 100,
            CorruptByteIndices: null,
            CorruptXorMask: 0,
            FaultId: "fault1"));

        await executor.ExecuteAsync(step, fakeCtx, CancellationToken.None);

        Assert.Single(fakeCtx.TaggedFaults);
        Assert.True(fakeCtx.TaggedFaults.ContainsKey("fault1_tx"));
    }

    /// <summary>
    /// Minimal IAssertionContext that does NOT implement IFaultInjectionContext.
    /// </summary>
    private sealed class FakeAssertionContext : IAssertionContext
    {
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotImplementedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public double CurrentTimestamp => 0;
        public System.Collections.Generic.IReadOnlyList<PeakCan.Host.Core.HIL.Contracts.DecodedFrame> GetRecentDecodedFrames() => Array.Empty<PeakCan.Host.Core.HIL.Contracts.DecodedFrame>();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotImplementedException();
    }
}
