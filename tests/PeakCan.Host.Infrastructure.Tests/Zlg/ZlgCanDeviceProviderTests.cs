using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.Devices;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Tests for <see cref="ZlgCanDeviceProvider"/>.
/// Uses the real <see cref="ZlgChannelEnumerator"/> (no hardware = empty channels).
/// </summary>
public sealed class ZlgCanDeviceProviderTests
{
    private static readonly string[] ExpectedBaudRates = { "125 kbps", "250 kbps", "500 kbps", "1 Mbps" };

    [Fact]
    public void EnumerateDevices_Always_ReturnsDeviceType()
    {
        // Arrange — real enumerator without hardware returns empty channels
        var provider = new ZlgCanDeviceProvider(
            new ZlgChannelEnumerator(NullLogger<ZlgChannelEnumerator>.Instance),
            NullLogger<ZlgCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert — device type always shows in UI even without hardware
        devices.Should().HaveCount(1);
        var d = devices[0];
        d.Id.Should().Be("zlg-usbcan-fd");
        d.DisplayName.Should().Be("USBCAN FD (ZLG)");
        d.SupportsFd.Should().BeTrue();
    }

    [Fact]
    public void EnumerateDevices_NoHardware_ReturnsDeviceWithEmptyChannels()
    {
        // Arrange
        var provider = new ZlgCanDeviceProvider(
            new ZlgChannelEnumerator(NullLogger<ZlgChannelEnumerator>.Instance),
            NullLogger<ZlgCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert
        devices.Should().HaveCount(1);
        // Without hardware, channels will be empty
        // DefaultHandle fallback = EncodeHandle(0, 0, 0) = 0x8000
        devices[0].DefaultHandle.Should().Be(0x8000);
    }

    [Fact]
    public void EnumerateDevices_BaudRateTable_IsComplete()
    {
        // Arrange
        var provider = new ZlgCanDeviceProvider(
            new ZlgChannelEnumerator(NullLogger<ZlgChannelEnumerator>.Instance),
            NullLogger<ZlgCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert
        var d = devices[0];
        d.BaudRates.Should().HaveCount(4);
        d.BaudRates.Select(b => b.Name).Should().Contain(ExpectedBaudRates);
        d.FdBaudRates.Should().HaveCount(3);
        d.DefaultBaudRate.Name.Should().Be("500 kbps");
    }

    [Fact]
    public void EnumerateDevices_DeviceDescriptor_HasCorrectDefaults()
    {
        // Arrange
        var provider = new ZlgCanDeviceProvider(
            new ZlgChannelEnumerator(NullLogger<ZlgChannelEnumerator>.Instance),
            NullLogger<ZlgCanDeviceProvider>.Instance);

        // Act
        var devices = provider.EnumerateDevices();

        // Assert
        var d = devices[0];
        d.Id.Should().Be("zlg-usbcan-fd");
        d.DisplayName.Should().Be("USBCAN FD (ZLG)");
        d.SupportsFd.Should().BeTrue();
        d.DefaultBaudRate.Name.Should().Be("500 kbps");
    }
}