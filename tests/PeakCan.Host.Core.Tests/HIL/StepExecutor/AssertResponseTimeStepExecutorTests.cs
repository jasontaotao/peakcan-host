using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

public class AssertResponseTimeStepExecutorTests
{
    private readonly FakeAssertionContext _ctx = new();
    private readonly AssertResponseTimeStepExecutor _executor = new();

    private static TestCaseStep CreateStep(CanId reqId, CanId respId, string maxMs) =>
        TestCaseStep.Create(new AssertResponseTimeStep(reqId, respId, maxMs));

    [Fact]
    public async Task ExecuteAsync_FastResponse_Passes()
    {
        // Arrange
        var step = CreateStep(new CanId(0x7DF, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), "100");
        var task = _executor.ExecuteAsync(step, _ctx, default);

        // Act: fire response frame after a short delay
        await Task.Delay(5);
        _ctx.PushFrame(new CanFrame(new CanId(0x7E8, FrameFormat.Standard),
            new byte[] { 0x06, 0x41, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Contains("Response in", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_SlowResponse_Fails()
    {
        // Arrange
        var step = CreateStep(new CanId(0x7DF, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), "20");
        var task = _executor.ExecuteAsync(step, _ctx, default);

        // Act: fire response after timeout
        await Task.Delay(50);
        _ctx.PushFrame(new CanFrame(new CanId(0x7E8, FrameFormat.Standard),
            new byte[] { 0x06 }, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StepStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_NoResponse_Timeout()
    {
        // Arrange - no frame will be fired
        var step = CreateStep(new CanId(0x7DF, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), "50");

        // Act
        var result = await _executor.ExecuteAsync(step, _ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("No response", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_SendFails_Fails()
    {
        // Arrange - context that fails on SendFrameAsync
        var failCtx = new FailingSendContext();
        var step = CreateStep(new CanId(0x7DF, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), "100");

        // Act
        var result = await _executor.ExecuteAsync(step, failCtx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("Failed to send", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ExternalCancel_Fails()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var step = CreateStep(new CanId(0x7DF, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), "5000");
        var task = _executor.ExecuteAsync(step, _ctx, cts.Token);

        // Act
        cts.CancelAfter(10);

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StepStatus.Failed, result.Status);
    }

    /// <summary>
    /// Fake context that always fails SendFrameAsync.
    /// </summary>
    private sealed class FailingSendContext : IAssertionContext
    {
        public double CurrentTimestamp => 0;
        public System.Collections.Generic.IReadOnlyList<PeakCan.HIL.Core.HIL.Contracts.DecodedFrame> GetRecentDecodedFrames() => Array.Empty<PeakCan.HIL.Core.HIL.Contracts.DecodedFrame>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => null!;
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) =>
            ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, "bus-off"));
    }
}
