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
            Written.Add(frame);
            return ValueTask.FromResult(NextResult);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SendService NewSvc() => new(NullLogger<SendService>.Instance);

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
}
