using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.Views;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.Replay;
using PeakCan.HIL.Core.Services;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL.Reporting;

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
/// <para>
/// v3.x (会话状态剥离 Task 2): Open/OpenRecent 命令改走
/// <see cref="ITraceSessionService"/>；Save 命令从缓存窗口取 VM。Save 的
/// "窗口已打开" 分支需要构造真实 <see cref="TraceViewerView"/>（引用 App.xaml
/// 的 BoolToVis / ColorToBrush 资源），故加入 <see cref="WpfAppTestCollection"/>
/// 以与其它创建 WPF Application 的测试类串行化。
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
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

    /// <summary>Test double for <see cref="PeakCan.HIL.Core.IChannelProbe"/>.</summary>
    private sealed class FakeChannelProbe : PeakCan.HIL.Core.IChannelProbe
    {
        public PeakCan.HIL.Core.ProbeResult Probe(ushort handle) =>
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
    private sealed class StubFileDialogService : PeakCan.HIL.Core.IFileDialogService
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
    /// and an <see cref="ITraceSessionService"/> substitute. v3.x
    /// (会话状态剥离 Task 2): Open/OpenRecent 命令改走 service，missing 列表由
    /// service 替身返回（原实现通过真实 registry 抛 FileNotFoundException 驱动）。
    /// </summary>
    private static AppShellViewModel MakeVm(
        IMessageBoxPrompt prompt,
        IFileDialogService fileDialogs,
        ITraceSessionService session)
    {
        var isoTp = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var udsClient = new UdsClient(isoTp);
        var recentTemp = Path.Combine(
            Path.GetTempPath(),
            $"recent-{Guid.NewGuid():N}.json");
        // ReplayViewModel 需要一条真实 TraceSessionLibrary 满足 ctor；本测试
        // 不触碰 Replay 保存路径，库文件不会真正写出。
        var replayLibraryPath = Path.Combine(
            Path.GetTempPath(),
            $"msgbox-replay-{Guid.NewGuid():N}.tmtrace");
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
            // v3.50.1 PATCH-A: RecordViewModel arg restored.
            new RecordViewModel(new RecordService(NullLogger<RecordService>.Instance), NullLogger<RecordViewModel>.Instance),
            new ReplayViewModel(
                Substitute.For<IReplayService>(),
                Substitute.For<IFileDialogService>(),
                Substitute.For<IAscContentHasher>(),
                Substitute.For<IAscLocator>(),
                NewRealSessionLibrary(replayLibraryPath),
                new RecentSessionsService(
                    NullLogger<RecentSessionsService>.Instance,
                    Path.Combine(Path.GetTempPath(), $"recent-{Guid.NewGuid():N}.json"))),
            new MultiFrameSendViewModel(new SequenceSendService(new SendService(NullLogger<SendService>.Instance))),
            // v3.x (会话状态剥离 Task 2): 会话命令走 service 替身；窗口 VM 工厂
            // 本测试不触发（ShowTraceViewerCommand 不执行）。
            session,
            () => Substitute.For<TraceViewerViewModel>(),
            new PeakCan.Host.App.Services.Trace.RecentSessionsService(
                NullLogger<PeakCan.Host.App.Services.Trace.RecentSessionsService>.Instance,
                recentTemp),
            fileDialogs,
            prompt,
            // Phase 4: HilViewModel ctor arg
            new HilViewModel(Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(), Substitute.For<IHilReportService>()),
            new EcuScriptEditorViewModel(Substitute.For<IFileDialogService>(), Substitute.For<IMessageBoxPrompt>(), NullLogger<EcuScriptEditorViewModel>.Instance));
    }

    /// <summary>Test double for <see cref="IChannelFactory"/>
    /// required by <see cref="AppShellViewModel"/> ctor. Never
    /// invoked by the missing-asc prompt tests.</summary>
    private sealed class FakeChannelFactory : PeakCan.HIL.Core.IChannelFactory
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
        // ARRANGE: 写一个引用缺失 .asc 的 .tmtrace bundle，并让 service 替身
        // 返回该缺失路径（OpenSessionAsync 的 missing 列表）。文件对话框返回
        // bundle 路径，OpenSessionCommand 走 service 后把 missing 列表路由到
        // IMessageBoxPrompt.ShowInformationAsync（标题 "Open Session"）。
        var bundlePath = WriteBundleWithMissingAsc(out var missingAscPath);
        var session = Substitute.For<ITraceSessionService>();
        session.OpenSessionAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { missingAscPath }));
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowInformationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.OK);
        var dialog = new StubFileDialogService { StubPath = bundlePath };
        var vm = MakeVm(prompt, dialog, session);

        // ACT
        await vm.OpenSessionCommand.ExecuteAsync(null);

        // ASSERT: 打开命令确实走 service（而非直接依赖 TraceViewerViewModel），
        // 且 missing 列表通过 IMessageBoxPrompt seam 弹出提示。We do NOT
        // assert the exact Window owner because tests run on MTA —
        // Application.Current is null.
        await session.Received(1).OpenSessionAsync(bundlePath);
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
        var bundlePath = WriteBundleWithMissingAsc(out var missingAscPath);
        var session = Substitute.For<ITraceSessionService>();
        session.OpenSessionAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { missingAscPath }));
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowInformationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.OK);
        var dialog = new StubFileDialogService { StubPath = "" }; // dialog unused here
        var vm = MakeVm(prompt, dialog, session);

        // ACT
        await vm.OpenRecentSessionCommand.ExecuteAsync(bundlePath);

        // ASSERT: same contract as OpenSessionAsync, but with the
        // "Open Recent Session" title to distinguish which menu
        // path triggered the warning.
        await session.Received(1).OpenSessionAsync(bundlePath);
        await prompt.Received(1).ShowInformationAsync(
            "Open Recent Session",
            Arg.Is<string>(m => m.Contains("missing") && m.Contains(".asc")),
            Arg.Any<Window?>());
    }

    /// <summary>
    /// Run <paramref name="body"/> on an STA thread (WPF Window ctor +
    /// Application require STA). Mirrors AppShellViewModelTests.RunSta.
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
            throw new TimeoutException("STA thread did not complete within 30 s — likely a WPF dispatcher deadlock");
        if (caught is not null) throw caught;
    }

    /// <summary>
    /// 清理历史泄漏的 WPF <see cref="Application"/>，并复位 .NET 10 WPF 的
    /// AppDomain 级创建守卫（<c>_appCreatedInThisAppDomain</c>）。
    /// <see cref="LeakedApplicationReset"/> 只清 <c>_appInstance</c>
    /// （Application.Current），不清该布尔守卫——若不复位，本 AppDomain 内
    /// 无法再创建第二个 Application（Save-with-window 用例需要新建）。
    /// </summary>
    private static void ResetAppDomainApplicationGuard()
    {
        LeakedApplicationReset.CleanupLeakedApplication();
        typeof(Application).GetField("_appCreatedInThisAppDomain",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, false);
    }

    [Fact]
    public async Task SaveSessionAsync_WithNoTraceViewerWindow_ShowsGuidance_AndDoesNotSave()
    {
        // v3.x (会话状态剥离 Task 2): SaveSessionAsync 需要从缓存窗口拿 VM。
        // 窗口未打开（_traceViewerView 为 null）→ 提示用户先打开 Trace Viewer，
        // 不执行保存（不会写 .tmtrace bundle 文件）。
        var session = Substitute.For<ITraceSessionService>();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowInformationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.OK);
        var savePath = Track(Path.Combine(_tempDir, "save-no-window.tmtrace"));
        var dialog = new StubFileDialogService { StubPath = savePath };
        var vm = MakeVm(prompt, dialog, session);

        // ACT
        await vm.SaveSessionCommand.ExecuteAsync(null);

        // ASSERT: 提示引导 + 未写文件。
        await prompt.Received(1).ShowInformationAsync(
            "Save Session",
            Arg.Is<string>(m => m.Contains("Trace Viewer")),
            Arg.Any<Window?>());
        File.Exists(savePath).Should().BeFalse(
            "窗口未打开时 SaveSessionCommand 不得写 bundle 文件");
    }

    [Fact]
    public async Task SaveSessionAsync_WithOpenTraceViewerWindow_SavesThroughWindowVm()
    {
        // v3.x (会话状态剥离 Task 2): 窗口已打开（_traceViewerView 非 null 且
        // DataContext 为 TraceViewerViewModel）→ SaveSessionAsync 走窗口 VM，
        // 真实写出 .tmtrace bundle。窗口构造需要 App.xaml 资源（BoolToVis /
        // ColorToBrush），在 STA 线程上先种 Application 再建窗；任务结束后清理
        // Application 单例（防泄漏竞态，见 LeakedApplicationReset）。Dispatcher
        // frame pump 确保异步命令在 STA 线程上完成（无论 SynchronizationContext
        // 是否被 Application 安装）。
        bool fileWritten = false;
        RunSta(() =>
        {
            try
            {
                ResetAppDomainApplicationGuard();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
                app.Resources["ColorToBrush"] = new ColorToBrushConverter();

                var session = Substitute.For<ITraceSessionService>();
                var prompt = Substitute.For<IMessageBoxPrompt>();
                var savePath = Track(Path.Combine(_tempDir, "save-with-window.tmtrace"));
                var dialog = new StubFileDialogService { StubPath = savePath };
                var shell = MakeVm(prompt, dialog, session);

                // 真实窗口 VM（sealed，不可替身）+ 真实窗口，塞进缓存字段。
                var windowVm = new TraceViewerViewModel(
                    Substitute.For<ITraceSessionRegistry>(),
                    new FakeDbcService(),
                    NullLogger<TraceViewerViewModel>.Instance,
                    NewFakeSessionLibrary());
                var win = new TraceViewerView(windowVm);
                typeof(AppShellViewModel)
                    .GetField("_traceViewerView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(shell, win);

                var saveTask = shell.SaveSessionCommand.ExecuteAsync(null);
                var frame = new System.Windows.Threading.DispatcherFrame();
                saveTask.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                fileWritten = File.Exists(savePath);
                prompt.Received(0).ShowInformationAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());
            }
            finally
            {
                ResetAppDomainApplicationGuard();
            }
        });

        fileWritten.Should().BeTrue(
            "窗口 DataContext 为 VM 时 SaveSessionCommand 应调用 vm.SaveSessionAsync 写出 bundle");
    }
}
