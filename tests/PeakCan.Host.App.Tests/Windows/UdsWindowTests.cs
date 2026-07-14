using System.Reflection;
using System.Windows;
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.Tests.ViewModels;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.Windows;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>
/// v3.11.3 PATCH: pins the ShowUdsCommand contract — the UDS surface
/// opens as a separate cached <see cref="UdsWindow"/> (not the in-place
/// <c>UdsView</c> UserControl used by v1.1.0 – v3.11.2). Mirrors the
/// ShowTraceViewer STA test pattern in <c>AppShellViewModelTests</c>.
/// STA-bound (Window ctor requires STA) — joined to
/// <see cref="WpfAppTestCollection"/> so it doesn't race on the WPF
/// Application singleton.
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class UdsWindowTests
{
    /// <summary>
    /// Hand-rolled <see cref="DbcService"/> stub. The shell only navigates
    /// into the UDS window; it never loads a DBC. Stub keeps the test
    /// hermetic (no real DBC file required).
    /// </summary>
    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
        public override System.Threading.Tasks.Task LoadAsync(string path, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Build a real <see cref="AppShellViewModel"/> with the same
    /// dependency surface the production DI uses. Mirrors
    /// <c>AppShellViewModelTests.NewVm</c> but is kept private here so
    /// the test file is self-contained (no internal visibility on the
    /// existing helper).
    /// </summary>
    private static AppShellViewModel NewVm()
    {
        var isoTp = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var udsClient = new UdsClient(isoTp);
        var recentTemp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"recent-uds-{System.Guid.NewGuid():N}.json");
        return new AppShellViewModel(
            new ChannelRouter(),
            NullLogger<AppShellViewModel>.Instance,
            new TraceViewModel(),
            new SendService(NullLogger<SendService>.Instance),
            new FakeChannelProbe(),
            new FakeChannelFactory(),
            new DbcViewModel(new FakeDbcService(),
                             new SignalViewModel(),
                             NullLogger<DbcViewModel>.Instance),
            new SendViewModel(new SendService(NullLogger<SendService>.Instance), NullLogger<SendViewModel>.Instance, new SendViewModelTests.FakeCyclicSendService(), null),
            new SignalViewModel(),
            new StatsViewModel(),
            new ScriptViewModel(NullLogger<ScriptViewModel>.Instance,
                                new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null)),
            new UdsViewModel(
                new SessionPanelViewModel(udsClient, NullLogger<SessionPanelViewModel>.Instance),
                new DidPanelViewModel(udsClient, new DidDatabase(NullLogger<DidDatabase>.Instance)),
                new RoutinePanelViewModel(udsClient, new RoutineDatabase(NullLogger<RoutineDatabase>.Instance)),
                new DtcPanelViewModel(udsClient)),
            // v3.50.1 PATCH-A: RecordViewModel arg restored (reverts v3.49 Q2).
            new RecordViewModel(new RecordService(NullLogger<RecordService>.Instance), NullLogger<RecordViewModel>.Instance),
            new ReplayViewModel(
                NSubstitute.Substitute.For<IReplayService>(),
                NSubstitute.Substitute.For<IFileDialogService>(),
                NSubstitute.Substitute.For<IAscContentHasher>(),
                NSubstitute.Substitute.For<IAscLocator>(),
                new TraceSessionLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"uds-tmtrace-{System.Guid.NewGuid():N}.tmtrace"), NullLogger<TraceSessionLibrary>.Instance),
                new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, recentTemp)),
            new MultiFrameSendViewModel(new SequenceSendService(new SendService(NullLogger<SendService>.Instance))),
            new TraceViewerViewModel(NSubstitute.Substitute.For<ITraceSessionRegistry>(), new FakeDbcService(), NullLogger<TraceViewerViewModel>.Instance,
                new TraceSessionLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"uds-traceview-{System.Guid.NewGuid():N}.tmtrace"), NullLogger<TraceSessionLibrary>.Instance)),
            new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, recentTemp),
            NSubstitute.Substitute.For<IFileDialogService>(),
            NSubstitute.Substitute.For<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>());
    }

    /// <summary>
    /// Hand-rolled <see cref="Core.IChannelFactory"/> stub. The production
    /// <see cref="AppShellViewModelTests"/> class declares the same shape
    /// as <c>private sealed class</c> — not visible here. Mirrored locally
    /// (duplication is the smaller cost vs the visibility surface change
    /// of promoting it to <c>internal</c> just for this PATCH).
    /// </summary>
    private sealed class FakeChannelFactory : Core.IChannelFactory
    {
        public ICanChannel Create(ChannelId id) => new FakeCanChannel(id);
    }

    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; } = true;
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
        // v3.16.9.4 PATCH: ICanChannel gained ReadLoopError event — unused
        // in this test fake, but must exist to satisfy the interface.
#pragma warning disable CS0067
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067
#pragma warning restore CS0067
        public FakeCanChannel(ChannelId id) { Id = id; }
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
            => Task.FromResult(Result<Unit>.Ok(default));
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            IsConnected = false;
        }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hand-rolled <see cref="Core.IChannelProbe"/> stub. Same shape as
    /// the <c>FakeChannelProbe</c> nested in <c>AppShellViewModelTests</c>
    /// (line 82); duplicated locally because the existing one is
    /// <c>private</c>.
    /// </summary>
    private sealed class FakeChannelProbe : Core.IChannelProbe
    {
        public Core.ProbeResult Probe(ushort handle)
            => new(true, $"fake probe ok 0x{handle:X2}");
    }

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
    public void ShowUdsCommand_Opens_Cached_UdsWindow()
    {
        // v3.11.3 PATCH: ShowUdsCommand opens a UdsWindow (Window) rather
        // than swapping CurrentView to a UdsView (UserControl). Cache reuse
        // mirrors ShowTraceViewer: a second click returns the same instance
        // so window position + SelectedDid + tab selections survive menu
        // round-trips.
        RunSta(() =>
        {
            var vm = NewVm();
            vm.ShowUdsCommand.Execute(null);

            var first = (UdsWindow?)typeof(AppShellViewModel)
                .GetField("_udsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm);
            first.Should().NotBeNull(
                "first ShowUdsCommand must populate the _udsWindow cache via ViewSwitcher.ShowWindow");

            vm.ShowUdsCommand.Execute(null);

            var second = (UdsWindow?)typeof(AppShellViewModel)
                .GetField("_udsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm);
            second.Should().BeSameAs(first,
                "second ShowUdsCommand must reuse the cached UdsWindow — matches ViewSwitcher.ShowWindow contract");

            // v3.11.3 PATCH: UdsWindow is no longer the in-place CurrentView
            // — it lives in its own Window. CurrentView must remain null
            // (the AppShell does not host a Uds tab any more).
            vm.CurrentView.Should().BeNull(
                "CurrentView is the in-place tab surface; UDS is now a Window, not a tab");
        });
    }
}