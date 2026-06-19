using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Views;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 12: verifies the <see cref="AppShellViewModel"/> initial state and
/// the no-hardware side of the open-DBC stub command. The probe/connect
/// commands exercise the PEAK SDK and are marked as integration+hardware
/// and skipped in CI; the WPF VM still compiles and instantiates in tests
/// so constructor injection is exercised.
/// <para>
/// Task 14: <see cref="AppShellViewModel"/> gained a 4th ctor parameter
/// (<see cref="SendService"/>) so the shell can publish the connected
/// channel to the manual-send form.
/// </para>
/// <para>
/// Task 15: <see cref="AppShellViewModel"/> gained a 5th ctor parameter
/// (<see cref="DbcViewModel"/>) plus three cached view instances
/// (<see cref="TraceView"/>, <see cref="DbcView"/> via the VM, plus the
/// existing <see cref="Views.SendView"/>). The shell owns view switching
/// via <see cref="AppShellViewModel.CurrentView"/> and three Show
/// commands. The Open DBC menu item now routes into the DbcViewModel
/// flow via <see cref="AppShellViewModel.OpenDbcCommand"/>.
/// </para>
/// </summary>
public class AppShellViewModelTests
{
    /// <summary>
    /// Hand-rolled <see cref="DbcService"/> stub. The real
    /// <see cref="Services.DbcService.LoadAsync"/> reads a file off disk,
    /// which is fine for AppShellViewModelTests because we never call it
    /// — the shell only navigates into the DBC view, it does not load
    /// the file. The stub keeps the test hermetic.
    /// </summary>
    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
        public override System.Threading.Tasks.Task LoadAsync(string path, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Test double for <see cref="Core.IChannelProbe"/>. Always returns
    /// a successful probe so the <c>CanConnect</c> predicate string
    /// ("USB1 ...") can be set by tests via <c>ChannelList = "..."</c>.
    /// </summary>
    private sealed class FakeChannelProbe : Core.IChannelProbe
    {
        public Core.ProbeResult Probe(ushort handle)
            => new(true, $"fake probe ok 0x{handle:X2}");
    }

    private static AppShellViewModel NewVm() =>
        new(new ChannelRouter(),
            NullLogger<AppShellViewModel>.Instance,
            new TraceViewModel(),
            new SendService(NullLogger<SendService>.Instance),
            new FakeChannelProbe(),
            new DbcViewModel(new FakeDbcService(),
                             new SignalViewModel(),
                             NullLogger<DbcViewModel>.Instance),
            new SendViewModel(new SendService(NullLogger<SendService>.Instance), NullLogger<SendViewModel>.Instance),
            new SignalViewModel(),
            new StatsViewModel());

    /// <summary>
    /// Run <paramref name="body"/> on an STA thread because the view
    /// switching tests instantiate WPF <c>UserControl</c>s. xunit defaults
    /// to MTA, which throws on every <c>FrameworkElement</c> ctor. Same
    /// pattern as <c>AppHostBuilderTests.RunSta</c>. Join uses a 30 s
    /// timeout so a stuck dispatcher never freezes the test runner.
    /// </summary>
    private static void RunSta(Action body)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA)
        {
            body();
            return;
        }
        Exception? caught = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (thread.IsAlive)
        {
            throw new TimeoutException("STA thread did not complete within 30 s — likely a WPF dispatcher deadlock");
        }
        if (caught is not null) throw caught;
    }

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

    // Task 15: the OpenDbcCommand stub behaviour (sets StatusMessage to
    // "Open DBC clicked (Task 15)") is gone — the command now switches
    // the ContentControl to the DbcView. The old
    // `OpenDbcCommand_Sets_StatusMessage_To_Stub_Message` test is
    // intentionally DELETED and replaced by `OpenDbcCommand_Now_Switches_To_DbcView`.

    [Fact]
    public void Default_CurrentView_Is_Null_Before_First_Show()
    {
        // Task 15: the shell's view instances are created lazily because
        // WPF UserControls require an STA thread (xunit runs on MTA).
        // Production wires the default tab via AppShell.xaml.cs's
        // SourceInitialized handler, which calls ShowTraceCommand.
        // Here in the test we just construct the VM and verify CurrentView
        // starts null — the Show tests below cover the actual rendering
        // switch.
        var vm = NewVm();
        vm.CurrentView.Should().BeNull();
    }

    [Fact]
    public void ShowTraceCommand_Sets_CurrentView_To_TraceView()
    {
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowDbcCommand.Execute(null);   // switch to DBC (lazy)
            vm.ShowTraceCommand.Execute(null); // back to trace (lazy)
            vm.CurrentView.Should().BeOfType<TraceView>();
        });
    }

    [Fact]
    public void ShowDbcCommand_Sets_CurrentView_To_DbcView()
    {
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowDbcCommand.Execute(null);
            vm.CurrentView.Should().BeOfType<DbcView>();
        });
    }

    [Fact]
    public void ShowSendCommand_Sets_CurrentView_To_SendView()
    {
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowSendCommand.Execute(null);
            vm.CurrentView.Should().BeOfType<SendView>();
        });
    }

    [Fact]
    public void ShowSignalsCommand_Sets_CurrentView_To_SignalView()
    {
        // Task 16: the Signal tab joins the existing Trace / DBC / Send
        // tabs. Mirror the ShowSendCommand test: lazy view instantiation
        // on first show.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowSignalsCommand.Execute(null);
            vm.CurrentView.Should().BeOfType<SignalView>();
        });
    }

    [Fact]
    public void ShowSignalsCommand_Reuses_Cached_SignalView_Instance()
    {
        // Caching the view preserves DataGrid virtualization state across
        // menu switches. A second Show call must return the same instance.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowSignalsCommand.Execute(null);
            var first = vm.CurrentView;
            vm.ShowTraceCommand.Execute(null);
            vm.ShowSignalsCommand.Execute(null);
            vm.CurrentView.Should().BeSameAs(first,
                "second Show should return the cached SignalView instance");
        });
    }

    [Fact]
    public void ShowStatsCommand_Sets_CurrentView_To_StatsView()
    {
        // Task 17: the Stats tab joins Trace / DBC / Send / Signals.
        // Lazy view instantiation on first show — same pattern as the
        // other tabs. StatsView hosts an OxyPlot.PlotView bound to
        // StatsViewModel.PlotModel.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowStatsCommand.Execute(null);
            vm.CurrentView.Should().BeOfType<StatsView>();
        });
    }

    [Fact]
    public void ShowStatsCommand_Reuses_Cached_StatsView_Instance()
    {
        // The OxyPlot PlotView holds internal state (axis ranges,
        // tracker overlays) that should survive a menu round-trip
        // to another tab. Caching the StatsView instance preserves
        // it. Same pattern as the other Show* tests.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowStatsCommand.Execute(null);
            var first = vm.CurrentView;
            vm.ShowTraceCommand.Execute(null);
            vm.ShowStatsCommand.Execute(null);
            vm.CurrentView.Should().BeSameAs(first,
                "second Show should return the cached StatsView instance");
        });
    }

    [Fact]
    public void OpenDbcCommand_Now_Switches_To_DbcView()
    {
        // The Open DBC menu item (File ▸ Open DBC...) is the user-facing
        // entry point. Per Task 15 it now navigates to the DBC tab
        // rather than stubbing a status message. The actual file open
        // happens inside DbcViewModel.OpenAsync via the per-view Open
        // button.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.OpenDbcCommand.Execute(null);
            vm.CurrentView.Should().BeOfType<DbcView>();
        });
    }

    [Fact]
    public void OpenDbcCommand_Does_Not_Reset_SignalView_Directly()
    {
        // Sanity check: opening DBC navigates the view but does not
        // itself clear SignalViewModel — that happens inside
        // DbcViewModel.OnLoaded once the DBC actually parses. So a
        // navigate-without-load must leave any prior decoded-signal
        // entries alone. The companion test (DbcViewModelTests) covers
        // the reset-on-load path directly.
        RunSta(() =>
        {
            var vm = NewVm();
            var signals = (SignalViewModel)typeof(AppShellViewModel)
                .GetField("_signalViewModel", System.Reflection.BindingFlags.Instance |
                                              System.Reflection.BindingFlags.NonPublic)!
                .GetValue(vm)!;
            signals.Latest.Add(new SignalEntry { Message = "M1", Signal = "S" });
            signals.Latest.Should().HaveCount(1);

            vm.OpenDbcCommand.Execute(null);

            signals.Latest.Should().HaveCount(1, "navigation alone does not reset the decoded-signal table");
        });
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

    [Fact(Skip = "Requires PEAK USB hardware (PCAN-USB FD on handle 0x51).")]
    [Trait("category", "integration")]
    [Trait("category", "hardware")]
    public void ConnectCommand_After_Successful_Probe_Wires_SendService_ActiveChannel()
    {
        // Task 14 wiring: a successful Connect publishes the active
        // ICanChannel onto SendService so the manual-send form can
        // transmit. Without PEAK hardware, the probe fails and the
        // connect path cannot run; covered manually.
        var svc = new SendService(NullLogger<SendService>.Instance);
        var vm = new AppShellViewModel(
            new ChannelRouter(),
            NullLogger<AppShellViewModel>.Instance,
            new TraceViewModel(),
            svc,
            new FakeChannelProbe(),
            new DbcViewModel(new FakeDbcService(),
                             new SignalViewModel(),
                             NullLogger<DbcViewModel>.Instance),
            new SendViewModel(svc, NullLogger<SendViewModel>.Instance),
            new SignalViewModel(),
            new StatsViewModel());
        vm.EnumerateChannelsCommand.Execute(null);
        vm.ConnectCommand.Execute(null);
        svc.ActiveChannel.Should().NotBeNull();
    }
}