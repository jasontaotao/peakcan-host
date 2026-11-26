using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>发送路径：通道句柄、FD/经典按帧分派、未连接与驱动失败。</summary>
public sealed class ZlgCanChannelWriteTests
{
    private static readonly ChannelId TestChannel = new(0xA900);

    private static CanFrame Frame(bool fd)
        => new(
            new CanId(0x123, FrameFormat.Standard),
            new byte[] { 1, 2, 3 },
            fd ? FrameFlags.Fd : FrameFlags.None,
            TestChannel,
            Timestamp.FromMicroseconds(0));

    private static async Task<(FakeZlgApi Api, ZlgCanChannel Channel)> ConnectedAsync(bool fd)
    {
        var api = new FakeZlgApi();
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        var manager = new ZlgDeviceManager(api);
        var channel = new ZlgCanChannel(TestChannel, manager, api);
        var result = await channel.ConnectAsync(fd ? BaudRate.CanFd2Mbps : BaudRate.Can500kbps, fd);
        result.IsSuccess.Should().BeTrue();
        return (api, channel);
    }

    [Fact]
    public async Task WriteClassic_UsesChannelHandle()
    {
        var (api, channel) = await ConnectedAsync(fd: false);

        var result = await channel.WriteAsync(Frame(fd: false));

        result.IsSuccess.Should().BeTrue();
        api.TransmitCalls.Should().ContainSingle();
        api.TransmitCalls[0].ChannelHandle.ToInt64().Should().Be(0x2000);
        api.TransmitFdCalls.Should().BeEmpty();

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task WriteFd_UsesChannelHandleAndTransmitFd()
    {
        var (api, channel) = await ConnectedAsync(fd: true);

        var result = await channel.WriteAsync(Frame(fd: true));

        result.IsSuccess.Should().BeTrue();
        api.TransmitFdCalls.Should().ContainSingle();
        api.TransmitFdCalls[0].ChannelHandle.ToInt64().Should().Be(0x2000);
        api.TransmitCalls.Should().BeEmpty();

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task WriteClassicOnFdChannel_UsesTransmitInsteadOfRejecting()
    {
        // ZLG 默认按 FD 连接；UDS/ISO-TP 与 DBC 经典报文都是经典帧。
        // 必须按帧类型分派，不能按通道模式硬拒。
        var (api, channel) = await ConnectedAsync(fd: true);

        var result = await channel.WriteAsync(Frame(fd: false));

        result.IsSuccess.Should().BeTrue();
        api.TransmitCalls.Should().ContainSingle();
        api.TransmitFdCalls.Should().BeEmpty();

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task WriteFdFrameOnClassicChannel_UsesTransmitFdInsteadOfRejecting()
    {
        var (api, channel) = await ConnectedAsync(fd: false);

        var result = await channel.WriteAsync(Frame(fd: true));

        result.IsSuccess.Should().BeTrue();
        api.TransmitFdCalls.Should().ContainSingle();
        api.TransmitCalls.Should().BeEmpty();

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task WriteWhenDisconnected_Fails()
    {
        var api = new FakeZlgApi();
        var manager = new ZlgDeviceManager(api);
        var channel = new ZlgCanChannel(TestChannel, manager, api);

        var result = await channel.WriteAsync(Frame(fd: false));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCode.InvalidState);
        api.TransmitCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteTransmitFailure_MapsError()
    {
        var (api, channel) = await ConnectedAsync(fd: false);
        api.TransmitResult = ZlgError.Failed;

        var result = await channel.WriteAsync(Frame(fd: false));

        result.IsSuccess.Should().BeFalse();
        api.TransmitCalls.Should().ContainSingle();

        await channel.DisconnectAsync();
    }
}
