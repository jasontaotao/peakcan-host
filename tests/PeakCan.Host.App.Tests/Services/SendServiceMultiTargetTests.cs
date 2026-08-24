using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Task 6 (phase 2 A-4): SendService multi-target routing tests.
/// SendAsync(frame, ChannelId) routes to the channel in the dictionary snapshot
/// (set via SetChannels), bypassing ActiveChannel. Falls back to ActiveChannel
/// when channelId is absent or not found (zero regression for the 6 legacy
/// senders that call SendAsync(frame)).
/// <para>
/// Uses a concrete FakeChannel (not NSubstitute) to avoid CA2012 ValueTask
/// warnings from <c>.Returns(ValueTask...)</c> — mirrors the existing
/// CyclicSendServiceRaceTests pattern.
/// </para>
/// </summary>
public sealed class SendServiceMultiTargetTests
{
#pragma warning disable CS0067 // FrameReceived unused in this recording fake
    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public List<CanFrame> WrittenFrames { get; } = new();
        public event Action<CanFrame>? FrameReceived;
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067
        public FakeChannel(ushort handle) { Id = new ChannelId(handle); }
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default) { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        { WrittenFrames.Add(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0067

    private static readonly CanFrame TestFrame = new(
        new CanId(0x100, FrameFormat.Standard),
        new byte[] { 0x01 }, FrameFlags.None, default, default);

    [Fact]
    public async Task SendAsync_ByChannelId_RoutesToThatChannel_NotActive()
    {
        var svc = new SendService(NullLogger<SendService>.Instance);
        var chA = new FakeChannel(0x51);
        var chB = new FakeChannel(0x52);
        svc.SetChannels(new Dictionary<ChannelId, ICanChannel>
        {
            [new(0x51)] = chA,
            [new(0x52)] = chB,
        });
        svc.ActiveChannel = chA; // 默认 bus-a

        await svc.SendAsync(TestFrame, new ChannelId(0x52), default); // 显式 bus-b

        Assert.Single(chB.WrittenFrames);
        Assert.Empty(chA.WrittenFrames); // 未走 ActiveChannel
    }

    [Fact]
    public async Task SendAsync_NoChannelId_FallsBackToActiveChannel()
    {
        // 零回归：无 channelId 重载（旧 SendAsync(frame, ct)）→ ActiveChannel
        var svc = new SendService(NullLogger<SendService>.Instance);
        var chA = new FakeChannel(0x51);
        svc.ActiveChannel = chA;
        // 不调 SetChannels（6 既有发送方不设字典快照）

        await svc.SendAsync(TestFrame, default);

        Assert.Single(chA.WrittenFrames);
    }

    [Fact]
    public async Task SendAsync_ByChannelId_NotInSnapshot_FallsBackToActive()
    {
        // channelId 不在字典快照 → 回落 ActiveChannel（尽力式，不硬失败）
        var svc = new SendService(NullLogger<SendService>.Instance);
        var chA = new FakeChannel(0x51);
        svc.ActiveChannel = chA;
        svc.SetChannels(new Dictionary<ChannelId, ICanChannel> { [new(0x51)] = chA });

        // 0x99 不在快照 → 回落 ActiveChannel (chA)
        await svc.SendAsync(TestFrame, new ChannelId(0x99), default);

        Assert.Single(chA.WrittenFrames);
    }
}
