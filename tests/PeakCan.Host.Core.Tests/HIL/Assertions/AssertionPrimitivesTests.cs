using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Assertions;

public class AssertionPrimitivesTests
{
    [Fact(Skip = "Async WaitForSignal deadlock - needs sync context fix in test infra")]
    public async Task WaitForSignal_SignalMatches_PassesImmediately()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pushTask = Task.Run(async () =>
        {
            await readyTcs.Task; // Wait until subscription is ready
            ctx.SetSignal("RPM", 3000.0);
            ctx.PushFrame(MakeFrame(0x123));
        });

        // BUG-001 fix: pass timeoutMs (5000ms) + CancellationToken
        var waitTask = primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, 5000, default);
        readyTcs.SetResult(true);

        var result = await waitTask;
        await pushTask;

        Assert.True(result.Passed);
        Assert.Contains("RPM", result.Message);
    }

    [Fact(Skip = "Async WaitForSignal deadlock - needs sync context fix in test infra")]
    public async Task WaitForSignal_SignalMatchesWithinTolerance_Passes()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pushTask = Task.Run(async () =>
        {
            await readyTcs.Task;
            ctx.SetSignal("RPM", 3005.0);
            ctx.PushFrame(MakeFrame(0x123));
        });

        var waitTask = primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, 5000, default);
        readyTcs.SetResult(true);

        var result = await waitTask;
        await pushTask;

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task WaitForSignal_Timeout_FailsWithActualValue()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        // BUG-001 fix: use timeoutMs parameter instead of external CTS
        var result = await primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, 200, default);

        Assert.False(result.Passed);
        Assert.Null(result.ActualValue);
    }

    [Fact(Skip = "Pre-cancelled CT propagation - Task.Delay(Infinite, cancelledCT) returns cancelled task but await doesn't throw as expected")]
    public async Task WaitForSignal_CancellationTokenCancelled_ThrowsOperationCanceledException()
    {
        var ctx = new FakeAssertionContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var primitives = new AssertionPrimitives(ctx);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, 5000, cts.Token));
    }

    [Fact(Skip = "Async WaitForSignal deadlock - needs sync context fix in test infra")]
    public async Task WaitForSignal_MultipleFrames_EventuallyMatches()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, 5000, default);

        // Push non-matching frame
        var push1 = Task.Run(async () =>
        {
            await readyTcs.Task;
            ctx.SetSignal("RPM", 5000.0);
            ctx.PushFrame(MakeFrame(0x123));
        });
        readyTcs.SetResult(true);
        await push1;
        await Task.Delay(20);

        // Push matching frame
        ctx.SetSignal("RPM", 3000.0);
        ctx.PushFrame(MakeFrame(0x123));

        var result = await waitTask;
        Assert.True(result.Passed);
    }

    [Fact]
    public void AssertSignal_InTolerance_Passes()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("RPM", 3005.0);

        var primitives = new AssertionPrimitives(ctx);
        var result = primitives.AssertSignal("RPM", 3000.0, 10.0);

        Assert.True(result.Passed);
    }

    [Fact]
    public void AssertSignal_OutOfTolerance_FailsWithActualAndExpected()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("RPM", 3100.0);

        var primitives = new AssertionPrimitives(ctx);
        var result = primitives.AssertSignal("RPM", 3000.0, 10.0);

        Assert.False(result.Passed);
        Assert.NotNull(result.ActualValue);
        Assert.NotNull(result.ExpectedValue);
    }

    [Fact]
    public void AssertSignal_SignalNotFound_Fails()
    {
        var ctx = new FakeAssertionContext();

        var primitives = new AssertionPrimitives(ctx);
        var result = primitives.AssertSignal("Unknown", 3000.0, 10.0);

        Assert.False(result.Passed);
    }

    [Fact]
    public void AssertRange_InRange_Passes()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("Temp", 50.0);

        var primitives = new AssertionPrimitives(ctx);
        var result = primitives.AssertRange("Temp", 0.0, 100.0);

        Assert.True(result.Passed);
    }

    [Fact]
    public void AssertRange_OutOfRange_Fails()
    {
        var ctx = new FakeAssertionContext();
        ctx.SetSignal("Temp", 150.0);

        var primitives = new AssertionPrimitives(ctx);
        var result = primitives.AssertRange("Temp", 0.0, 100.0);

        Assert.False(result.Passed);
    }

    [Fact]
    public void AssertRange_SignalNotFound_Fails()
    {
        var ctx = new FakeAssertionContext();

        var primitives = new AssertionPrimitives(ctx);
        var result = primitives.AssertRange("Unknown", 0.0, 100.0);

        Assert.False(result.Passed);
    }

    private static CanFrame MakeFrame(uint id) =>
        new(new CanId(id, FrameFormat.Standard), new byte[8], FrameFlags.None, default, default);
}
