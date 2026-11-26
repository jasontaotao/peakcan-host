using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>设备描述符：通道、DefaultHandle、波特率表。</summary>
public sealed class ZlgCanDeviceProviderTests
{
    private static readonly string[] ExpectedBaudRates = { "125 kbps", "250 kbps", "500 kbps", "1 Mbps" };

    private static ZlgCanDeviceProvider Provider(FakeZlgApi api)
    {
        var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);
        return new ZlgCanDeviceProvider(enumerator, NullLogger<ZlgCanDeviceProvider>.Instance);
    }

    [Fact]
    public void EnumerateDevices_AlwaysReturnsZlgDeviceFamily()
    {
        var devices = Provider(new FakeZlgApi()).EnumerateDevices();

        devices.Should().ContainSingle();
        devices[0].Id.Should().Be("zlg-usbcan-fd");
        devices[0].DisplayName.Should().Be("USBCAN FD (ZLG)");
        devices[0].SupportsFd.Should().BeTrue();
    }

    [Fact]
    public void EnumerateDevices_NoHardware_EmptyChannelsAndZeroDefaultHandle()
    {
        var devices = Provider(new FakeZlgApi()).EnumerateDevices();

        devices[0].Channels.Should().BeEmpty();
        devices[0].DefaultHandle.Should().Be(0, "no channel exists, so there is no valid default handle");
    }

    [Fact]
    public void EnumerateDevices_WithDevice_ExposesChannelsAndDefaultHandle()
    {
        var api = new FakeZlgApi { CanNum = 2 };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;

        var devices = Provider(api).EnumerateDevices();

        devices[0].Channels.Should().HaveCount(2);
        devices[0].DefaultHandle.Should().Be(0xA900);
    }

    [Fact]
    public void EnumerateDevices_BaudRateTableIsComplete()
    {
        var device = Provider(new FakeZlgApi()).EnumerateDevices()[0];

        device.BaudRates.Should().HaveCount(4);
        device.BaudRates.Select(b => b.Name).Should().Contain(ExpectedBaudRates);
        device.FdBaudRates.Should().HaveCount(3);
        device.DefaultBaudRate.Name.Should().Be("500 kbps");
    }
}
