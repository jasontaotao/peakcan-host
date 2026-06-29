using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Task 14: <see cref="SendService"/> is the manual-send entry point. It
/// holds a single <see cref="ICanChannel"/> reference (MVP) and forwards
/// <see cref="SendService.SendAsync"/> calls to it. When the channel is
/// null (pre-connect), it returns a failed <see cref="Result{T}"/> with
/// <see cref="ErrorCode.InvalidState"/> so the UI can surface a clear
/// "connect first" message instead of an unhandled <see cref="NullReferenceException"/>.
/// <para>
/// These tests use a hand-written <see cref="FakeChannel"/> matching the
/// pattern from <c>SinkWiringServiceTests</c>; this keeps App tests free
/// of NSubstitute-only constructs and matches the project's testing style.
/// </para>
/// </summary>
public class SendServiceTests
{
    /// <summary>
    /// Minimal <see cref="ICanChannel"/> that records every frame passed
    /// to <see cref="WriteAsync"/> and returns a configurable
    /// <see cref="Result{T}"/>. Only the surface the
    /// <see cref="SendService"/> actually touches is implemented.
    /// </summary>
    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public List<CanFrame> Written { get; } = new();
        public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);
        public FakeChannel(ChannelId id) { Id = id; IsConnected = true; }
#pragma warning disable CS0067 // Event is never raised; SendService never subscribes in these tests
        public event Action<CanFrame>? FrameReceived;
#pragma warning restore CS0067
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Written.Add(frame);
            return ValueTask.FromResult(NextResult);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SendService NewSvc() => new(NullLogger<SendService>.Instance);

    /// <summary>
    /// Channel fixture that unconditionally throws
    /// <see cref="OperationCanceledException"/> with a caller-supplied
    /// CT on every <see cref="WriteAsync"/>. Used by
    /// <see cref="SendAsync_channel_WriteAsync_OCE_with_unrelated_CT_does_not_swallow"/>
    /// to verify that SendService does NOT rewrite or swallow an OCE
    /// that originated from the channel layer with an unrelated CT.
    /// Mirrors v1.6.2 PATCH process lesson 4: do not mask unrelated
    /// cancellations.
    /// </summary>
    private sealed class OceFakeChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        private readonly CancellationToken _oceToken;
        public OceFakeChannel(ChannelId id, CancellationToken oceToken)
        {
            Id = id;
            IsConnected = true;
            _oceToken = oceToken;
        }
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
#pragma warning restore CS0067
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
            => throw new OperationCanceledException(_oceToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void ActiveChannel_Defaults_To_Null()
    {
        // STRING-COUPLED: a non-null default would make pre-connect sends
        // hit a dead channel and obscure the wiring bug we're trying to
        // prevent (i.e. AppShell.ConnectAsync failing to populate the
        // service). The MVP contract is "null until Connect succeeds".
        var svc = NewSvc();
        svc.ActiveChannel.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_With_Null_Channel_Returns_InvalidState_Failure()
    {
        var svc = NewSvc();
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.None,
            ChannelId.None,
            default);

        var result = await svc.SendAsync(frame);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be(ErrorCode.InvalidState);
        result.Error.Message.Should().Contain("No active channel");
    }

    [Fact]
    public async Task SendAsync_Delegates_To_Channel_WriteAsync_With_Frame()
    {
        var channel = new FakeChannel(new ChannelId(0x51));
        var svc = NewSvc();
        svc.ActiveChannel = channel;
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.Fd,
            ChannelId.None,
            default);

        var result = await svc.SendAsync(frame);

        result.IsSuccess.Should().BeTrue();
        channel.Written.Should().HaveCount(1);
        channel.Written[0].Should().Be(frame);
    }

    [Fact]
    public async Task ActiveChannel_Setter_Updates_Property_And_Is_Reflected_In_Subsequent_Sends()
    {
        // Two distinct channels: pre-set to ch1, swap to ch2, send one
        // frame, assert it landed on ch2. Confirms the setter is not
        // snapshotted at construction.
        var ch1 = new FakeChannel(new ChannelId(0x51));
        var ch2 = new FakeChannel(new ChannelId(0x52));
        var svc = NewSvc();
        svc.ActiveChannel = ch1;
        svc.ActiveChannel = ch2;

        svc.ActiveChannel.Should().BeSameAs(ch2);

        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty,
            FrameFlags.None,
            ChannelId.None,
            default);
        await svc.SendAsync(frame);

        ch1.Written.Should().BeEmpty("the swapped-out channel must not receive frames");
        ch2.Written.Should().HaveCount(1);
    }

    [Fact]
    public async Task SendAsync_Propagates_Channel_Failure_Result()
    {
        // The service is a passthrough on the success path AND on the
        // failure path; it must not wrap or transform the channel's
        // Result. The UI layer relies on receiving the original code.
        var channel = new FakeChannel(new ChannelId(0x51))
        {
            NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "USB unplugged")
        };
        var svc = NewSvc();
        svc.ActiveChannel = channel;
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty,
            FrameFlags.None,
            ChannelId.None,
            default);

        var result = await svc.SendAsync(frame);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCode.HardwareNotAvailable);
        result.Error.Message.Should().Be("USB unplugged");
    }

    [Fact]
    public async Task SendAsync_propagates_cancelled_CT_to_channel_WriteAsync()
    {
        // v1.6.3 PATCH Item 1 (RED): SendService.SendAsync must
        // propagate the caller-supplied CancellationToken to
        // ICanChannel.WriteAsync. When the CT is already cancelled,
        // the channel layer must observe a cancelled CT and throw OCE;
        // SendService must not suppress it. This is the most
        // fundamental behavior of the CT parameter on SendAsync and was
        // uncovered prior to v1.6.3.
        var channel = new FakeChannel(new ChannelId(0x51));
        var svc = NewSvc();
        svc.ActiveChannel = channel;
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.None,
            ChannelId.None,
            default);
        using var preCancelledCts = new CancellationTokenSource();
        preCancelledCts.Cancel();

        Func<Task> act = async () => { await svc.SendAsync(frame, preCancelledCts.Token); };

        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.CancellationToken.Should().Be(preCancelledCts.Token);
    }

    [Fact]
    public async Task SendAsync_channel_WriteAsync_OCE_with_unrelated_CT_does_not_swallow()
    {
        // v1.6.3 PATCH Item 1 (defensive): SendService must NOT rewrite
        // or filter an OperationCanceledException that originated from
        // the channel layer. If the channel throws OCE with a CT that
        // differs from the caller-supplied CT (e.g. an internal network
        // timeout, hardware shutdown, downstream ISO-TP cancellation),
        // SendService must let the OCE propagate untouched, preserving
        // the channel's original CT on ex.CancellationToken. This guards
        // against a future regression where someone "helpfully" adds a
        // catch (OperationCanceledException) without a `when` filter
        // (v1.6.2 PATCH process lesson 4 — release notes line 81
        // "audit the catch chain for OCE" + MEMORY.md recap
        // "always pair `catch (OCE)` with `when (ct.IsCancellationRequested)`").
        using var unrelatedCts = new CancellationTokenSource();
        unrelatedCts.Cancel();
        var unrelatedCt = unrelatedCts.Token;

        var channel = new OceFakeChannel(new ChannelId(0x51), unrelatedCt);
        var svc = NewSvc();
        svc.ActiveChannel = channel;
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty,
            FrameFlags.None,
            ChannelId.None,
            default);
        using var freshCts = new CancellationTokenSource();

        Func<Task> act = async () => { await svc.SendAsync(frame, freshCts.Token); };

        var assertion = await act.Should().ThrowAsync<OperationCanceledException>();
        assertion.Which.CancellationToken.Should().Be(unrelatedCt);
    }
}
