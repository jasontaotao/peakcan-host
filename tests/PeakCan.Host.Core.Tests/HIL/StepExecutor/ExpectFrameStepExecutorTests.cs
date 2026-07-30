using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.HIL.Fakes;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;

public class ExpectFrameStepExecutorTests
{
    private readonly FakeAssertionContext _ctx = new();
    private readonly ExpectFrameStepExecutor _executor;

    public ExpectFrameStepExecutorTests() =>
        _executor = new ExpectFrameStepExecutor(new AssertionPrimitives(_ctx));

    [Fact]
    public async Task ExecuteAsync_FrameReceived_ReturnsPassed()
    {
        // Arrange
        var step = TestCaseStep.Create(
            new ExpectFrameStep(new CanId(0x123, FrameFormat.Standard), null, 1000));
        var task = _executor.ExecuteAsync(step, _ctx, default);

        // Act
        _ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Contains("0x123", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsFailed()
    {
        // Arrange - no frame will be fired
        var step = TestCaseStep.Create(
            new ExpectFrameStep(new CanId(0x123, FrameFormat.Standard), null, 50));

        // Act
        var result = await _executor.ExecuteAsync(step, _ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("timeout", result.Message);
    }
}
