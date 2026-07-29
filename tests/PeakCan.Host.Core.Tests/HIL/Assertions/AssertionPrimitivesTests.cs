using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Assertions;

public class AssertionPrimitivesTests
{
    [Fact]
    public async Task WaitForSignal_SignalMatches_PassesImmediately()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);

        // Push frame from background thread to avoid sync deadlock
        var pushTask = Task.Run(() =>
        {
            ctx.SetSignal("RPM", 3000.0);
            ctx.PushFrame(MakeFrame(0x123));
        });

        var result = await primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, default);
        await pushTask;

        Assert.True(result.Passed);
        Assert.Contains("RPM", result.Message);
    }

    [Fact]
    public async Task WaitForSignal_SignalMatchesWithinTolerance_Passes()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);

        var pushTask = Task.Run(() =>
        {
            ctx.SetSignal("RPM", 3005.0);
            ctx.PushFrame(MakeFrame(0x123));
        });

        var result = await primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, default);
        await pushTask;

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task WaitForSignal_Timeout_FailsWithActualValue()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, cts.Token);

        Assert.False(result.Passed);
        Assert.Null(result.ActualValue);
    }

    [Fact]
    public async Task WaitForSignal_CancellationTokenCancelled_ThrowsOperationCanceledException()
    {
        var ctx = new FakeAssertionContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var primitives = new AssertionPrimitives(ctx);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, cts.Token));
    }

    [Fact]
    public async Task WaitForSignal_MultipleFrames_EventuallyMatches()
    {
        var ctx = new FakeAssertionContext();
        var primitives = new AssertionPrimitives(ctx);

        // Push non-matching frame from background
        var push1 = Task.Run(() =>
        {
            ctx.SetSignal("RPM", 5000.0);
            ctx.PushFrame(MakeFrame(0x123));
        });
        await push1;
        await Task.Delay(20);

        // Push matching frame from background
        var push2 = Task.Run(() =>
        {
            ctx.SetSignal("RPM", 3000.0);
            ctx.PushFrame(MakeFrame(0x123));
        });

        var result = await primitives.WaitForSignalAsync("RPM", 3000.0, 10.0, default);
        await push2;

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
