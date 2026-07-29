using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;

public class StepExecutorTests
{
    [Fact]
    public async Task WaitForSignalExecutor_ReturnsPassed_WhenSignalMatches()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("RPM", 3000.0);

        var executor = new WaitForSignalStepExecutor(new AssertionPrimitives(ctx));
        var step = TestCaseStep.Create(new WaitForSignalStep("RPM", 3000.0, 10.0, 5000));

        // Push matching frame from background
        var push = Task.Run(() =>
        {
            ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[8], FrameFlags.None, default, default));
            ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard), new byte[8], FrameFlags.None, default, default));
        });

        var result = await executor.ExecuteAsync(step, ctx, default);
        await push;

        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task WaitForSignalExecutor_CreatesLinkedCTS_ForTimeout()
    {
        var ctx = new FakeAssertionContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var executor = new WaitForSignalStepExecutor(new AssertionPrimitives(ctx));
        var step = TestCaseStep.Create(new WaitForSignalStep("RPM", 3000.0, 10.0, 5000));

        var result = await executor.ExecuteAsync(step, ctx, cts.Token);

        Assert.Equal(StepStatus.Failed, result.Status);
    }

    [Fact]
    public async Task AssertSignalExecutor_ReturnsPassed_WhenInTolerance()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("RPM", 3005.0);

        var executor = new AssertSignalStepExecutor(new AssertionPrimitives(ctx));
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));

        var result = await executor.ExecuteAsync(step, ctx, default);

        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task AssertSignalExecutor_ReturnsFailed_WhenOutOfTolerance()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("RPM", 3100.0);

        var executor = new AssertSignalStepExecutor(new AssertionPrimitives(ctx));
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));

        var result = await executor.ExecuteAsync(step, ctx, default);

        Assert.Equal(StepStatus.Failed, result.Status);
    }

    [Fact]
    public async Task AssertRangeExecutor_ReturnsPassed_WhenInRange()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("Temp", 50.0);

        var executor = new AssertRangeStepExecutor(new AssertionPrimitives(ctx));
        var step = TestCaseStep.Create(new AssertRangeStep("Temp", 0.0, 100.0));

        var result = await executor.ExecuteAsync(step, ctx, default);

        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task SendFrameExecutor_CallsSendFrameAsync()
    {
        var ctx = new FakeAssertionContext();

        var executor = new SendFrameStepExecutor();
        var step = TestCaseStep.Create(new SendFrameStep(new CanId(0x7DF, FrameFormat.Standard), new byte[] { 0x02, 0x10, 0x03 }, false, false));

        var result = await executor.ExecuteAsync(step, ctx, default);

        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Single(ctx.SentFrames);
        Assert.Equal(0x7DFu, ctx.SentFrames[0].Id.Raw);
    }

    [Fact]
    public async Task DelayExecutor_ReturnsPassed_AfterDelay()
    {
        var ctx = new FakeAssertionContext();

        var executor = new DelayStepExecutor();
        var step = TestCaseStep.Create(new DelayStep(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await executor.ExecuteAsync(step, ctx, default);
        sw.Stop();

        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.True(sw.ElapsedMilliseconds >= 40, $"Delay too short: {sw.ElapsedMilliseconds}ms");
    }

    [Fact(Skip = "SendSequence has no StepParameters subclass in Sprint 1")]
    public async Task SendSequenceExecutor_Throws_NotSupportedException()
    {
        // Sprint 2: will test with proper step
    }
}
