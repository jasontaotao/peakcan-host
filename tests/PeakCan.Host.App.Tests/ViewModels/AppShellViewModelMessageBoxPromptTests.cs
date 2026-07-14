using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.10.0 MINOR T1 (C1): pins the contract that
/// <see cref="AppShellViewModel.OpenSessionCommand"/> and
/// <see cref="AppShellViewModel.OpenRecentSessionCommand"/>
/// route their missing-.asc modal through
/// <see cref="IMessageBoxPrompt.ShowInformationAsync"/> — NOT
/// <c>MessageBox.Show</c> directly. Pre-T1, both call sites called
/// <c>MessageBox.Show(...)</c> at the VM layer, which made the
/// commands impossible to unit-test (no STA modal in xunit). The
/// fix introduces an <see cref="IMessageBoxPrompt"/> seam, wired
/// in production by <see cref="WpfMessageBoxPrompt"/> and faked
/// in tests by NSubstitute.
/// </summary>
public sealed class AppShellViewModelMessageBoxPromptTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public AppShellViewModelMessageBoxPromptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"msgbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in _files)
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string Track(string p) { _files.Add(p); return p; }

    /// <summary>Test double for <see cref="Core.IChannelProbe"/>.</summary>
    private sealed class FakeChannelProbe : Core.IChannelProbe
    {
        public Core.ProbeResult Probe(ushort handle) =>
            new(true, $"fake probe ok 0x{handle:X2}");
    }

    /// <summary>Hand-rolled <see cref="DbcService"/> stub so
    /// <c>DbcViewModel</c> ctor succeeds without reading a file.</summary>
    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
        public override System.Threading.Tasks.Task LoadAsync(
            string path, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Test double for <see cref="IFileDialogService"/> that
    /// always returns <paramref name="stubPath"/>. Drives the
    /// <c>OpenSessionCommand</c> down the "user picked a path"
    /// branch.</summary>
    private sealed class StubFileDialogService : Core.IFileDialogService
    {
        public string StubPath { get; set; } = string.Empty;
        public string ShowOpenDialog(string filter) => StubPath;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory)
            => StubPath;
    }

    private static TraceSessionLibrary NewFakeSessionLibrary() =>
        new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

    private static TraceSessionLibrary NewRealSessionLibrary(string path) =>
        new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

    /// <summary>
    /// MakeVm factory: takes an explicit <see cref="IFileDialogService"/>
    /// and a real <see cref="TraceSessionLibrary"/> bound to the VM's
    /// ctor. The sessionLibraryPath is what the VM will see when
    /// <c>OpenSessionCommand</c> calls <c>TraceViewerViewModel.OpenSessionAsync</c>.
    /// </summary>
    private static AppShellViewModel MakeVm(
        IMessageBoxPrompt prompt,
        IFileDialogService fileDialogs,
        string sessionLibraryPath)
    {
        var isoTp = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var udsClient = new UdsClient(isoTp);
        var recentTemp = Path.Combine(
            Path.GetTempPath(),
            $"recent-{Guid.NewGuid():N}.json");
        // Registry must throw FileNotFoundException for missing
        // .asc files (matches real TraceSessionRegistry behavior).
        // ApplySnapshotAsync catches this and adds the path to the
        // missing list, which is what drives the IMessageBoxPrompt
        // seam in AppShellViewModel.OpenSessionAsync.
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<TraceSource>(_ =>
                throw new FileNotFoundException(
                    "fake registry: asc file does not exist",
                    _.ArgAt<string>(0)));
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
            new SendViewModel(new SendService(NullLogger<SendService>.Instance),
                              NullLogger<SendViewModel>.Instance,
                              new SendViewModelTests.FakeCyclicSendService(),
                              null),
            new SignalViewModel(),
            new StatsViewModel(),
            new ScriptViewModel(NullLogger<ScriptViewModel>.Instance,
                                new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null)),
            new UdsViewModel(
                new SessionPanelViewModel(udsClient, NullLogger<SessionPanelViewModel>.Instance),
                new DidPanelViewModel(udsClient, new DidDatabase(NullLogger<DidDatabase>.Instance)),
                new RoutinePanelViewModel(udsClient, new RoutineDatabase(NullLogger<RoutineDatabase>.Instance)),
                new DtcPanelViewModel(udsClient)),
            new ReplayViewModel(
                Substitute.For<IReplayService>(),
                Substitute.For<IFileDialogService>(),
                Substitute.For<IAscContentHasher>(),
                Substitute.For<IAscLocator>(),
                NewRealSessionLibrary(sessionLibraryPath),
                new RecentSessionsService(
                    NullLogger<RecentSessionsService>.Instance,
                    Path.Combine(Path.GetTempPath(), $"recent-{Guid.NewGuid():N}.json"))),
            new MultiFrameSendViewModel(new SequenceSendService(new SendService(NullLogger<SendService>.Instance))),
            new TraceViewerViewModel(
                registry,
                new FakeDbcService(),
                NullLogger<TraceViewerViewModel>.Instance,
                NewRealSessionLibrary(sessionLibraryPath)),
            new PeakCan.Host.App.Services.Trace.RecentSessionsService(
                NullLogger<PeakCan.Host.App.Services.Trace.RecentSessionsService>.Instance,
                recentTemp),
            fileDialogs,
            prompt);
    }

    /// <summary>Test double for <see cref="IChannelFactory"/>
    /// required by <see cref="AppShellViewModel"/> ctor. Never
    /// invoked by the missing-asc prompt tests.</summary>
    private sealed class FakeChannelFactory : Core.IChannelFactory
    {
        public ICanChannel Create(ChannelId id) => new FakeCanChannel(id);
    }

    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
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
    /// Build a real .tmtrace bundle on disk whose only source
    /// references a non-existent .asc file. Used to drive
    /// <c>OpenSessionCommand</c> down the "missing asc → IMessageBoxPrompt"
    /// path.
    /// </summary>
    private string WriteBundleWithMissingAsc(out string missingAscPath)
    {
        missingAscPath = Path.Combine(_tempDir, $"never-exists-{Guid.NewGuid():N}.asc");
        var bundlePath = Track(Path.Combine(_tempDir, $"bundle-{Guid.NewGuid():N}.tmtrace"));
        var dto = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            DbcPath = "",
            GlobalCanIdFilter = "",
            Playback = new BundlePlaybackDto
            {
                MasterSourceId = "src1",
                Speed = 1.0,
            },
            Sources = new List<BundleSourceDto>
            {
                new()
                {
                    SourceId = "src1",
                    DisplayName = "missing",
                    Path = missingAscPath,
                    ColorA = 255,
                    ColorR = 0xAA,
                    ColorG = 0xBB,
                    ColorB = 0xCC,
                    StrokeStyle = "Solid",
                    CanIdFilter = "",
                    ContentHash = "",
                }
            },
        };
        var lib = NewRealSessionLibrary(bundlePath);
        lib.Save(dto);
        return bundlePath;
    }

    [Fact]
    public async Task OpenSessionAsync_MissingAscFiles_RoutesThroughMessageBoxPrompt()
    {
        // ARRANGE: write a .tmtrace on disk that references a
        // non-existent .asc, then stub the file dialog to return
        // that .tmtrace path. The OpenSessionCommand will load
        // the bundle, find the .asc missing, and route through
        // IMessageBoxPrompt.ShowInformationAsync with title
        // "Open Session".
        var bundlePath = WriteBundleWithMissingAsc(out _);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowInformationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.OK);
        var dialog = new StubFileDialogService { StubPath = bundlePath };
        var vm = MakeVm(prompt, dialog, bundlePath);

        // ACT
        await vm.OpenSessionCommand.ExecuteAsync(null);

        // ASSERT: the IMessageBoxPrompt seam fired with the
        // expected "Open Session" title and a message that
        // mentions the missing .asc path. We do NOT assert
        // the exact Window owner because tests run on MTA —
        // Application.Current is null. The IMessageBoxPrompt
        // seam is the unit-testable replacement.
        await prompt.Received(1).ShowInformationAsync(
            "Open Session",
            Arg.Is<string>(m => m.Contains("missing") && m.Contains(".asc")),
            Arg.Any<Window?>());
    }

    [Fact]
    public async Task OpenRecentSessionAsync_MissingAscFiles_RoutesThroughMessageBoxPrompt()
    {
        // Mirror of OpenSessionAsync_MissingAscFiles_RoutesThroughMessageBoxPrompt.
        // OpenRecentSessionCommand takes the path directly (no
        // file dialog) and uses the "Open Recent Session" title.
        var bundlePath = WriteBundleWithMissingAsc(out _);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowInformationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.OK);
        var dialog = new StubFileDialogService { StubPath = "" }; // dialog unused here
        var vm = MakeVm(prompt, dialog, bundlePath);

        // ACT
        await vm.OpenRecentSessionCommand.ExecuteAsync(bundlePath);

        // ASSERT: same contract as OpenSessionAsync, but with the
        // "Open Recent Session" title to distinguish which menu
        // path triggered the warning.
        await prompt.Received(1).ShowInformationAsync(
            "Open Recent Session",
            Arg.Is<string>(m => m.Contains("missing") && m.Contains(".asc")),
            Arg.Any<Window?>());
    }
}
