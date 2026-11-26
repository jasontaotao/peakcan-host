using System.Reflection;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 读循环：两个队列都排空、时间戳按微秒、getReceiveNum 为 0 时不调用 Receive。
/// </summary>
public sealed class ZlgCanChannelReadLoopTests
{
    private static readonly ChannelId TestChannel = new(0xA900);

    [Fact]
    public async Task ReadLoop_DrainsBothClassicAndFdQueues()
    {
        var api = new FakeZlgApi
        {
            GetReceiveNumCanResult = 1,
            GetReceiveNumFdResult = 1,
            ReceiveResult = 1,
            ReceiveFdResult = 1,
            ClassicFrameFactory = () => new ZcanReceiveData
            {
                Frame = new ZcanCanFrame
                {
                    CanId = 0x123,
                    CanDlc = 2,
                    Data = new byte[] { 0xAA, 0xBB, 0, 0, 0, 0, 0, 0 },
                },
                Timestamp = 1_500,
            },
            FdFrameFactory = () => new ZcanReceiveFdData
            {
                Frame = new ZcanCanFdFrame
                {
                    CanId = 0x456,
                    Len = 3,
                    Flags = ZcanCanFdFlags.Brs,
                    Data = new byte[64],
                },
                Timestamp = 2_500,
            },
        };

        var channel = CreateConnectedChannel(api);
        var frames = new List<CanFrame>();
        channel.FrameReceived += frames.Add;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        await channel.ReadLoopAsync(cts.Token);

        frames.Should().Contain(f => !f.IsFd
            && f.Id.Raw == 0x123
            && f.Timestamp == Timestamp.FromMicroseconds(1_500));
        frames.Should().Contain(f => f.IsFd
            && f.Id.Raw == 0x456
            && f.Flags.HasFlag(FrameFlags.BitRateSwitch)
            && f.Timestamp == Timestamp.FromMicroseconds(2_500));
    }

    [Fact]
    public async Task ReadLoop_EmptyQueues_DoesNotCallReceive()
    {
        var api = new FakeZlgApi
        {
            GetReceiveNumCanResult = 0,
            GetReceiveNumFdResult = 0,
        };
        var channel = CreateConnectedChannel(api);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await channel.ReadLoopAsync(cts.Token);

        api.ReceiveCalls.Should().BeEmpty();
        api.ReceiveFdCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadLoop_GivesUp_MarksChannelDisconnected()
    {
        var api = new FakeZlgApi
        {
            GetReceiveNumCanResult = 1,
            GetReceiveNumFdResult = 0,
            ReceiveException = new InvalidOperationException("Simulated bus-off"),
        };
        var channel = CreateConnectedChannel(api);
        channel.IsConnected.Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await channel.ReadLoopAsync(cts.Token);

        channel.IsConnected.Should().BeFalse(
            "give-up 后必须标记断开，否则 UI 显示已连接但总线已死");
    }

    private static ZlgCanChannel CreateConnectedChannel(FakeZlgApi api)
    {
        var manager = new ZlgDeviceManager(api);
        var channel = new ZlgCanChannel(TestChannel, manager, api);
        SetField(channel, "_connected", true);
        SetField(channel, "_channelHandle", (IntPtr)0x2000);
        return channel;
    }

    private static void SetField<T>(ZlgCanChannel channel, string name, T value)
    {
        var field = typeof(ZlgCanChannel).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"ZlgCanChannel must have a private field {name}");
        field!.SetValue(channel, value);
    }
}
