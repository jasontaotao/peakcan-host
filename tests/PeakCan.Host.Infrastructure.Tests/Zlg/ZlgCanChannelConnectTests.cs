using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Connect/Disconnect 的调用顺序、句柄语义、FD/经典分支与失败回滚。
/// </summary>
public sealed class ZlgCanChannelConnectTests
{
    private static (FakeZlgApi Api, ZlgDeviceManager Manager) Setup()
    {
        var api = new FakeZlgApi();
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        return (api, new ZlgDeviceManager(api));
    }

    [Fact]
    public async Task Connect_Classic_SetsAbitThenInitThenStart()
    {
        var (api, manager) = Setup();
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var result = await channel.ConnectAsync(BaudRate.Can500kbps, fd: false);

        result.IsSuccess.Should().BeTrue();
        channel.IsConnected.Should().BeTrue();

        api.SetValueCalls.Should().ContainSingle();
        api.SetValueCalls[0].Path.Should().Be("0/canfd_abit_baud_rate");
        api.SetValueCalls[0].Value.Should().Be("500000");

        api.InitCanCalls.Should().ContainSingle();
        api.InitCanCalls[0].DeviceHandle.ToInt64().Should().Be(0x1000);
        api.InitCanCalls[0].CanIndex.Should().Be(0);
        api.InitCanCalls[0].Config.CanType.Should().Be(ZcanCanType.Can);

        api.StartCanCalls.Should().ContainSingle();
        api.StartCanCalls[0].ToInt64().Should().Be(0x2000);

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task Connect_Fd_ConfiguresArbitrationDataRateAndIsoStandard()
    {
        var (api, manager) = Setup();
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var result = await channel.ConnectAsync(BaudRate.CanFd2Mbps, fd: true);

        result.IsSuccess.Should().BeTrue();
        api.SetValueCalls.Select(c => c.Path).Should().Equal(
            "0/canfd_abit_baud_rate",
            "0/canfd_dbit_baud_rate",
            "0/canfd_standard");
        api.SetValueCalls.Select(c => c.Value).Should().Equal("1000000", "2000000", "0");
        api.InitCanCalls[0].Config.CanType.Should().Be(ZcanCanType.CanFd);

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task Connect_OpenFails_ReturnsHardwareNotAvailable()
    {
        var api = new FakeZlgApi();
        var manager = new ZlgDeviceManager(api);
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var result = await channel.ConnectAsync(BaudRate.Can500kbps, fd: false);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCode.HardwareNotAvailable);
        channel.IsConnected.Should().BeFalse();
        api.InitCanCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Connect_InitFails_ReleasesDevice()
    {
        var (api, manager) = Setup();
        api.InitCanHandle = IntPtr.Zero;
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var result = await channel.ConnectAsync(BaudRate.Can500kbps, fd: false);

        result.IsSuccess.Should().BeFalse();
        channel.IsConnected.Should().BeFalse();
        api.StartCanCalls.Should().BeEmpty();
        api.CloseCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Connect_StartFails_ResetsAndReleases()
    {
        var (api, manager) = Setup();
        api.StartCanResult = ZlgError.Failed;
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var result = await channel.ConnectAsync(BaudRate.Can500kbps, fd: false);

        result.IsSuccess.Should().BeFalse();
        channel.IsConnected.Should().BeFalse();
        api.ResetCanCalls.Should().ContainSingle().Which.ToInt64().Should().Be(0x2000);
        api.CloseCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Connect_SetAbitFailsInFdMode_ReleasesWithoutInit()
    {
        var (api, manager) = Setup();
        api.SetValueResult = ZlgError.Failed;
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var result = await channel.ConnectAsync(BaudRate.CanFd2Mbps, fd: true);

        result.IsSuccess.Should().BeFalse();
        api.InitCanCalls.Should().BeEmpty();
        api.CloseCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Disconnect_ResetsChannelBeforeClosingDevice()
    {
        var (api, manager) = Setup();
        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);
        await channel.ConnectAsync(BaudRate.Can500kbps, fd: false);

        api.Calls.Clear();
        await channel.DisconnectAsync();

        api.Calls.Should().Equal("reset", "close");
    }

    [Fact]
    public async Task ConcurrentConnect_SecondCallFailsAndFirstStillConnects()
    {
        var (api, manager) = Setup();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        api.BeforeInitCan = () =>
        {
            entered.Set();
            _ = release.Wait(TimeSpan.FromSeconds(5));
        };

        var channel = new ZlgCanChannel(new ChannelId(0xA900), manager, api);

        var firstTask = Task.Run(async () =>
            await channel.ConnectAsync(BaudRate.Can500kbps, fd: false));

        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        var second = await channel.ConnectAsync(BaudRate.Can500kbps, fd: false);

        second.IsSuccess.Should().BeFalse();
        second.Error!.Code.Should().Be(ErrorCode.InvalidState);
        second.Error!.Message.Should().Contain("connecting");

        release.Set();
        var first = await firstTask;
        first.IsSuccess.Should().BeTrue();
        channel.IsConnected.Should().BeTrue();

        await channel.DisconnectAsync();
    }

    [Fact]
    public async Task TwoChannels_ShareDevice_CloseOnlyAfterLastDisconnect()
    {
        var (api, manager) = Setup();
        var first = new ZlgCanChannel(new ChannelId(0xA900), manager, api);
        var second = new ZlgCanChannel(new ChannelId(0xA901), manager, api);

        await first.ConnectAsync(BaudRate.Can500kbps, fd: false);
        await second.ConnectAsync(BaudRate.Can500kbps, fd: false);

        api.OpenCalls.Should().ContainSingle();
        await first.DisconnectAsync();
        api.CloseCalls.Should().BeEmpty();

        await second.DisconnectAsync();
        api.CloseCalls.Should().ContainSingle();
    }
}
