using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Pure unit tests for the connect/disconnect state machine. The gate
/// owns the lock + CancellationTokenSource lifecycle that the read loop
/// reads; if the gate is wrong the read loop either leaks CTS or sees
/// a cancelled-but-disposed token. See design §3.1 (Sprint 17 plan).
/// </summary>
public class ChannelConnectGateTests
{
    [Fact]
    public void IsConnected_Starts_False()
    {
        var g = new ChannelConnectGate();
        g.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void TryEnter_From_Disconnected_State_Returns_Ok_And_Flips_IsConnected()
    {
        var g = new ChannelConnectGate();
        var r = g.TryEnter(CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        g.IsConnected.Should().BeTrue();
    }

    [Fact]
    public void TryEnter_When_Already_Connected_Returns_InvalidState()
    {
        var g = new ChannelConnectGate();
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
        var r = g.TryEnter(CancellationToken.None);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.InvalidState);
    }

    [Fact]
    public void TryEnter_With_Already_Cancelled_Token_Throws_And_Stays_Disconnected()
    {
        var g = new ChannelConnectGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => g.TryEnter(cts.Token);
        act.Should().Throw<OperationCanceledException>();
        g.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void MarkFailed_After_TryEnter_Returns_Gate_To_Disconnected()
    {
        var g = new ChannelConnectGate();
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
        g.MarkFailed();
        g.IsConnected.Should().BeFalse();
        // Gate must be reusable: a fresh TryEnter after MarkFailed succeeds.
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void MarkFailed_When_Not_Connected_Is_NoOp()
    {
        var g = new ChannelConnectGate();
        g.MarkFailed(); // must not throw
        g.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void SetReadLoop_Stores_Task_For_Later_Retrieval()
    {
        var g = new ChannelConnectGate();
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
        var tcs = new TaskCompletionSource();
        g.SetReadLoop(tcs.Task);
        g.CurrentReadLoop.Should().BeSameAs(tcs.Task);
    }

    [Fact]
    public void CaptureForDisconnect_Cancels_Cts_And_Returns_Token_And_Loop()
    {
        var g = new ChannelConnectGate();
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
        var tcs = new TaskCompletionSource();
        g.SetReadLoop(tcs.Task);
        var (token, loop) = g.CaptureForDisconnect();
        token.IsCancellationRequested.Should().BeTrue();
        loop.Should().BeSameAs(tcs.Task);
        g.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void CaptureForDisconnect_When_Not_Connected_Returns_Null_Loop()
    {
        var g = new ChannelConnectGate();
        var (token, loop) = g.CaptureForDisconnect();
        token.IsCancellationRequested.Should().BeFalse();
        loop.Should().BeNull();
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        var g = new ChannelConnectGate();
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
        g.Dispose();
        var act = () => g.Dispose();
        act.Should().NotThrow("dispose must be idempotent so WPF teardown and DI dispose can both call it");
    }

    [Fact]
    public void Dispose_After_Disconnect_Clears_Cts()
    {
        var g = new ChannelConnectGate();
        g.TryEnter(CancellationToken.None).IsSuccess.Should().BeTrue();
        g.CaptureForDisconnect();
        g.Dispose(); // must not throw ObjectDisposedException
    }

    [Fact]
    public async Task Concurrent_TryEnter_From_Many_Threads_Yields_Exactly_One_Success()
    {
        // Reproduces H1: many threads racing into ConnectAsync.
        // Exactly one must win; the rest see InvalidState.
        var g = new ChannelConnectGate();
        const int N = 64;
        var ok = 0;
        var fail = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, N), async (_, _) =>
        {
            var r = g.TryEnter(CancellationToken.None);
            if (r.IsSuccess) Interlocked.Increment(ref ok);
            else Interlocked.Increment(ref fail);
            await Task.CompletedTask;
        });
        ok.Should().Be(1);
        fail.Should().Be(N - 1);
        g.IsConnected.Should().BeTrue();
    }
}