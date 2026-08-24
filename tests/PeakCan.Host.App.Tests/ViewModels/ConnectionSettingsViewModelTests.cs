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

        // Assert — T4: ApplyAndConnect now calls ApplyConnections (list form).
        // The DIM default ApplyConnection forwards to it; single-group path
        // yields a 1-element list, behaviorally equivalent to the pre-T4 call.
        sink.Received(1).ApplyConnections(
            Arg.Is<IReadOnlyList<ConnectionConfig>>(list =>
                list.Count == 1 && list[0].Channel != null && list[0].Channel!.Handle == 0x52 && list[0].IsFd));
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

        // Assert — null channel in the single-group config; Connect still fired.
        sink.Received(1).ApplyConnections(
            Arg.Is<IReadOnlyList<ConnectionConfig>>(list =>
                list.Count == 1 && list[0].Channel == null));
        sink.Received(1).Connect();
    }

    // ── Task 4: 多通道弹窗 UI（A-2）──────────

    [Fact]
    public void AddChannel_IncrementsRows_RemoveChannel_Decrements()
    {
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(new[] { new ChannelInfo(0x51, "CH0") });
        var vm = NewVm(sink);

        vm.ExtraRows.Should().BeEmpty();
        vm.AddChannelCommand.Execute(null);
        vm.ExtraRows.Should().HaveCount(1);
        vm.AddChannelCommand.Execute(null);
        vm.ExtraRows.Should().HaveCount(2);

        var first = vm.ExtraRows[0];
        vm.RemoveChannelCommand.Execute(first);
        vm.ExtraRows.Should().HaveCount(1);
        vm.ExtraRows.Should().NotContain(first);
    }

    [Fact]
    public void ApplyAndConnect_MultipleRows_CollectsAllConfigs_ToSink()
    {
        // 首组（VM 单组字段）+ 1 额外行 → ApplyConnections 收到 2 个 config。
        var sink = Substitute.For<IConnectSettingsSink>();
        sink.AvailableChannels.Returns(new[]
        {
            new ChannelInfo(0x51, "CH0"),
            new ChannelInfo(0x52, "CH1"),
        });
        var vm = NewVm(sink);
        // 首组：CH1 (0x52)
        vm.SelectedChannel = vm.Channels[1];

        // 额外行 1
        vm.AddChannelCommand.Execute(null);
        var row = vm.ExtraRows[0];
        row.SelectedChannel = row.Channels[0]; // CH0 → 0x51

        vm.ApplyAndConnectCommand.Execute(null);

        sink.Received(1).ApplyConnections(
            Arg.Is<IReadOnlyList<ConnectionConfig>>(list =>
                list.Count == 2
                && list.Any(c => c.Channel != null && c.Channel!.Handle == 0x52)
                && list.Any(c => c.Channel != null && c.Channel!.Handle == 0x51)));
        sink.Received(1).Connect();
    }
}
