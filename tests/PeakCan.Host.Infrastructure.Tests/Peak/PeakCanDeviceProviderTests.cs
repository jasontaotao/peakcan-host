using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Devices;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Peak;

/// <summary>
/// P1-1: pins <see cref="PeakCanDeviceProvider"/> behaviors:
/// <list type="number">
/// <item>enumerates one PEAK device whose channels come from IChannelEnumerator;</item>
/// <item>the default handle is the first enumerated channel (fallback 0x51);</item>
/// <item>no hardware → device with empty channels, no throw;</item>
/// <item>null enumerator (no DI wiring) → still returns the device, no throw;</item>
/// <item>capability table is complete (classic + FD bitrates, FD supported).</item>
/// </list>
/// </summary>
public sealed class PeakCanDeviceProviderTests
{
    private static readonly string[] ClassicRateNames =
        { "125 kbps", "250 kbps", "500 kbps", "1 Mbps" };

    [Fact]
    public void EnumerateDevices_ReturnsPeakDevice_WithEnumeratedChannels()
    {
        // Arrange
        var enumerator = Substitute.For<IChannelEnumerator>();
        enumerator.Enumerate().Returns(new[]
        {
            new ChannelInfo(0x51, "CH0"),
            new ChannelInfo(0x52, "CH1"),
        });
        var provider = new PeakCanDeviceProvider(enumerator, NullLogger<PeakCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert
        devices.Should().HaveCount(1);
        var d = devices[0];
        d.Id.Should().Be("peak-usb-fd");
        d.DisplayName.Should().Be("PCAN-USB FD (PEAK)");
        d.SupportsFd.Should().BeTrue();
        d.Channels.Should().HaveCount(2);
        d.Channels[0].Handle.Should().Be(0x51);
        d.Channels[0].Name.Should().Be("CH0");
        d.DefaultHandle.Should().Be(0x51);
        d.BaudRates.Should().HaveCount(4);
        d.FdBaudRates.Should().HaveCount(3);
    }

    [Fact]
    public void EnumerateDevices_NoHardware_ReturnsDeviceWithEmptyChannels_DoesNotThrow()
    {
        // Arrange
        var enumerator = Substitute.For<IChannelEnumerator>();
        enumerator.Enumerate().Returns(Array.Empty<ChannelInfo>());
        var provider = new PeakCanDeviceProvider(enumerator, NullLogger<PeakCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert
        devices.Should().HaveCount(1);
        devices[0].Channels.Should().BeEmpty();
        devices[0].DefaultHandle.Should().Be(PeakCanDeviceProvider.PcanUsbFdFirstHandle,
            "with no enumerated hardware the provider falls back to the first PEAK handle");
    }

    [Fact]
    public void EnumerateDevices_NullEnumerator_DoesNotThrow()
    {
        // Arrange — no enumerator wired (legacy single-channel path).
        var provider = new PeakCanDeviceProvider(null, NullLogger<PeakCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert
        devices.Should().HaveCount(1);
        devices[0].Channels.Should().BeEmpty();
    }

    [Fact]
    public void EnumerateDevices_DefaultBaudRate_Is500kbps()
    {
        // Arrange
        var provider = new PeakCanDeviceProvider(null, NullLogger<PeakCanDeviceProvider>.Instance);

        // Act
        var d = provider.EnumerateDevices()[0];

        // Assert
        d.DefaultBaudRate.Name.Should().Be("500 kbps");
    }

    [Fact]
    public void EnumerateDevices_ClassicAndFdBaudRates_ArePopulated()
    {
        // Arrange
        var provider = new PeakCanDeviceProvider(null, NullLogger<PeakCanDeviceProvider>.Instance);

        // Act
        var d = provider.EnumerateDevices()[0];

        // Assert — classic list has the four presets; FD list is populated too.
        d.BaudRates.Select(b => b.Name).Should().Contain(ClassicRateNames);
        d.FdBaudRates.Select(b => b.Name).Should().Contain("2 Mbps (FD)");
    }
}
