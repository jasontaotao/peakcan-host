using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Devices;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P1-2: pins <see cref="ConnectionSettingsViewModel"/> behaviors:
/// <list type="number">
/// <item>ctor populates devices from providers and selects the first;</item>
/// <item>device change drives channels + FD bitrate list + RateLabel;</item>
/// <item>toggling FD switches the bitrate list between data-phase and classic;</item>
/// <item>ApplyAndConnect writes the selection to the sink (channel matched by handle);</item>
/// <item>no handle match → sink receives null channel (Connect still fires).</item>
/// </list>
/// </summary>
public sealed class ConnectionSettingsViewModelTests
{
    private static readonly BaudRate[] ClassicRates =
        { BaudRate.Can125kbps, BaudRate.Can250kbps, BaudRate.Can500kbps, BaudRate.Can1Mbps };
    private static readonly BaudRate[] FdRates =
        { BaudRate.CanFd1Mbps, BaudRate.CanFd2Mbps, BaudRate.CanFd5Mbps };

    private static DeviceDescriptor FakeDevice() => new(
        Id: "test-box",
        DisplayName: "测试 CAN 盒",
        Channels: new[] { new ChannelDescriptor(0x51, "CH0"), new ChannelDescriptor(0x52, "CH1") },
        BaudRates: ClassicRates,
        SupportsFd: true,
        FdBaudRates: FdRates,
        DefaultHandle: 0x51,
        DefaultBaudRate: BaudRate.Can500kbps);

    private sealed class FakeProvider : ICanDeviceProvider
    {
        private readonly DeviceDescriptor _device;
        public FakeProvider(DeviceDescriptor device) => _device = device;
        public IReadOnlyList<DeviceDescriptor> EnumerateDevices() => new[] { _device };
    }

    private static ConnectionSettingsViewModel NewVm(IConnectSettingsSink sink) =>
        new(new ICanDeviceProvider[] { new FakeProvider(FakeDevice()) }, sink,
            NullLogger<ConnectionSettingsViewModel>.Instance);

    [Fact]
    public void Ctor_PopulatesDevices_SelectsFirst_AndDrivesFieldsFromDescriptor()
    {
        // Arrange
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(Array.Empty<ChannelInfo>());

        // Act
        var vm = NewVm(sink);

        // Assert
        vm.Devices.Should().HaveCount(1);
        vm.SelectedDevice.Should().NotBeNull();
        vm.Channels.Should().HaveCount(2);
        vm.SelectedChannel.Should().BeSameAs(vm.Channels[0]);
        vm.IsFd.Should().BeTrue("device supports FD → default on");
        vm.AvailableBaudRates.Should().Equal(FdRates);
        vm.SelectedBaudRate.Should().Be(FdRates[0]);
        vm.RateLabel.Should().Be("数据段速率");
        sink.Received(1).ProbeChannels();
    }

    [Fact]
    public void ToggleIsFd_SwitchesBaudRateList_AndRateLabel()
    {
        // Arrange
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(Array.Empty<ChannelInfo>());
        var vm = NewVm(sink);

        // Act
        vm.IsFd = false;

        // Assert — classic list + label flip.
        vm.AvailableBaudRates.Should().Equal(ClassicRates);
        vm.RateLabel.Should().Be("波特率");

        // Act — back to FD.
        vm.IsFd = true;
        vm.AvailableBaudRates.Should().Equal(FdRates);
    }

    [Fact]
    public void SelectOtherDevice_ReloadsChannels_FromDescriptor()
    {
        // Arrange
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(Array.Empty<ChannelInfo>());
        var vm = NewVm(sink);
        var other = new DeviceDescriptor(
            "box2", "第二个盒子", new[] { new ChannelDescriptor(0x61, "A") },
            ClassicRates, false, Array.Empty<BaudRate>(), 0x61, BaudRate.Can500kbps);

        // Act
        vm.SelectedDevice = other;

        // Assert
        vm.Channels.Should().HaveCount(1);
        vm.SelectedChannel!.Handle.Should().Be(0x61);
        vm.IsFd.Should().BeFalse("box2 does not support FD");
        vm.AvailableBaudRates.Should().Equal(ClassicRates);
    }

    [Fact]
    public void ApplyAndConnect_MatchesChannelByHandle_WritesToSink_AndConnects()
    {
        // Arrange
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(new[]
        {
            new ChannelInfo(0x51, "CH0"),
            new ChannelInfo(0x52, "CH1"),
        });
        var vm = NewVm(sink);
        vm.SelectedChannel = vm.Channels[1]; // CH1 → handle 0x52
        vm.SelectedBaudRate = FdRates[1];
        vm.IsFd = true;

        // Act
        vm.ApplyAndConnectCommand.Execute(null);

        // Assert
        sink.Received(1).ApplyConnection(
            Arg.Is<ChannelInfo>(c => c.Handle == 0x52),
            FdRates[1],
            true);
        sink.Received(1).Connect();
    }

    [Fact]
    public void ApplyAndConnect_NoHandleMatch_PassesNullChannel_StillConnects()
    {
        // Arrange — shell has no channels (nothing matched the picker).
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(Array.Empty<ChannelInfo>());
        var vm = NewVm(sink);

        // Act
        vm.ApplyAndConnectCommand.Execute(null);

        // Assert — null channel written through; Connect still fired.
        sink.Received(1).ApplyConnection(null, Arg.Any<BaudRate>(), true);
        sink.Received(1).Connect();
    }
}
