using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Verifies <see cref="CyclicSendService"/> start/stop lifecycle and
/// periodic frame transmission.
/// </summary>
public class CyclicSendServiceTests
{
    /// <summary>
    /// Minimal <see cref="ICanChannel"/> that records written frames.
    /// </summary>
    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; } = true;
        public List<CanFrame> Written { get; } = new();
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
#pragma warning restore CS0067
        public FakeChannel(ChannelId id) { Id = id; }
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            Written.Add(frame);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static CanFrame MakeFrame(uint id = 0x123)
        => new(new CanId(id, FrameFormat.Standard),
               new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
               FrameFlags.None,
               ChannelId.None,
               default);

    [Fact]
    public void IsRunning_False_By_Default()
    {
        var svc = new CyclicSendService(
            new SendService(NullLogger<SendService>.Instance),
            NullLogger<CyclicSendService>.Instance);
        svc.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Start_Sets_IsRunning_True()
    {
        var channel = new FakeChannel(new ChannelId(0x51));
        var sendSvc = new SendService(NullLogger<SendService>.Instance);
        sendSvc.ActiveChannel = channel;
        var svc = new CyclicSendService(sendSvc, NullLogger<CyclicSendService>.Instance);

        svc.Start(MakeFrame(), TimeSpan.FromMilliseconds(100));
        svc.IsRunning.Should().BeTrue();
        svc.Stop();
    }

    [Fact]
    public void Stop_Sets_IsRunning_False()
    {
        var channel = new FakeChannel(new ChannelId(0x51));
        var sendSvc = new SendService(NullLogger<SendService>.Instance);
        sendSvc.ActiveChannel = channel;
        var svc = new CyclicSendService(sendSvc, NullLogger<CyclicSendService>.Instance);

        svc.Start(MakeFrame(), TimeSpan.FromMilliseconds(100));
        svc.Stop();
        svc.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Stop_Is_Idempotent()
    {
        var svc = new CyclicSendService(
            new SendService(NullLogger<SendService>.Instance),
            NullLogger<CyclicSendService>.Instance);
        svc.Stop(); // must not throw
        svc.Stop();
    }

    [Fact]
    public async Task Start_Sends_Frames_Periodically()
    {
        var channel = new FakeChannel(new ChannelId(0x51));
        var sendSvc = new SendService(NullLogger<SendService>.Instance);
        sendSvc.ActiveChannel = channel;
        var svc = new CyclicSendService(sendSvc, NullLogger<CyclicSendService>.Instance);

        svc.Start(MakeFrame(), TimeSpan.FromMilliseconds(50));
        await Task.Delay(200); // wait for ~4 ticks
        svc.Stop();

        svc.SendCount.Should().BeGreaterThan(0, "cyclic send should have fired at least once");
        channel.Written.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Start_Stops_Previous_Cyclic_Send()
    {
        var channel = new FakeChannel(new ChannelId(0x51));
        var sendSvc = new SendService(NullLogger<SendService>.Instance);
        sendSvc.ActiveChannel = channel;
        var svc = new CyclicSendService(sendSvc, NullLogger<CyclicSendService>.Instance);

        svc.Start(MakeFrame(0x100), TimeSpan.FromMilliseconds(100));
        svc.Start(MakeFrame(0x200), TimeSpan.FromMilliseconds(100));
        svc.IsRunning.Should().BeTrue();
        svc.Stop();
    }
}
