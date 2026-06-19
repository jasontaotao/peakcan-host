using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 12: verifies the <see cref="AppShellViewModel"/> initial state and
/// the no-hardware side of the open-DBC stub command. The probe/connect
/// commands exercise the PEAK SDK and are marked as integration+hardware
/// and skipped in CI; the WPF VM still compiles and instantiates in tests
/// so constructor injection is exercised.
/// </summary>
public class AppShellViewModelTests
{
    private static AppShellViewModel NewVm() => new(new ChannelRouter(), NullLogger<AppShellViewModel>.Instance);

    [Fact]
    public void Default_State_Is_Disconnected_With_Ready_Status()
    {
        var vm = NewVm();
        vm.ConnectionState.Should().Be("Disconnected");
        vm.IsConnected.Should().BeFalse();
        vm.StatusMessage.Should().Be("Ready");
    }

    [Fact]
    public void Default_ChannelList_Indicates_User_Must_Probe()
    {
        var vm = NewVm();
        vm.ChannelList.Should().Contain("Probe");
    }

    [Fact]
    public void OpenDbcCommand_Sets_StatusMessage_To_Stub_Message()
    {
        var vm = NewVm();
        vm.OpenDbcCommand.Execute(null);
        vm.StatusMessage.Should().Contain("DBC");
    }

    [Fact]
    public void ConnectCommand_Is_Disabled_Before_Probe_Succeeds()
    {
        // Before any successful probe, the user has no idea which channels
        // are available. Connect must not be invokable in that state.
        var vm = NewVm();
        vm.ConnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ConnectCommand_Is_Enabled_After_Probe_Sets_Usb1_Sentinel()
    {
        // STRING-COUPLED: the "USB1 ..." sentinel below mirrors the probe-
        // success message emitted by EnumerateChannels (and the predicate
        // in AppShellViewModel.CanConnect). A future change to that
        // message must update both sides. The [ObservableProperty]
        // ChannelList setter fires the ConnectCommand's CanExecute
        // re-evaluation via [NotifyCanExecuteChangedFor].
        var vm = NewVm();
        vm.ChannelList = "USB1 (1 Mbps default)";
        vm.ConnectCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact(Skip = "Requires PEAK USB hardware (PCAN-USB FD on handle 0x51).")]
    [Trait("category", "integration")]
    [Trait("category", "hardware")]
    public void EnumerateChannelsCommand_Without_Hardware_Sets_ChannelList_To_Error_Message()
    {
        // Hardware-dependent path. With no PEAK device attached, the probe
        // fails and ChannelList should report an error. Skipped in CI; run
        // manually on a workstation with a PCAN-USB FD plugged in.
        var vm = NewVm();
        vm.EnumerateChannelsCommand.Execute(null);
        vm.ChannelList.Should().Contain("No PEAK hardware detected");
    }

    [Fact(Skip = "Requires PEAK USB hardware (PCAN-USB FD on handle 0x51).")]
    [Trait("category", "integration")]
    [Trait("category", "hardware")]
    public void ConnectCommand_After_Probe_Attaches_ChannelRouter()
    {
        // Hardware-dependent path. After a successful probe, Connect should
        // be invokable, the underlying ICanChannel should be registered on
        // the ChannelRouter, and IsConnected should flip to true.
        var vm = NewVm();
        vm.EnumerateChannelsCommand.Execute(null);
        vm.ConnectCommand.CanExecute(null).Should().BeTrue();
        vm.ConnectCommand.Execute(null);
        vm.IsConnected.Should().BeTrue();
    }
}
