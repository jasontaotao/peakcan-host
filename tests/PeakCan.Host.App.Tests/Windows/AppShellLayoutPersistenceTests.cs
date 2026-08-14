using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.Tests.ViewModels;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.Replay;
using PeakCan.HIL.Core.Services;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using Xunit;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>
/// P2-6: AppShell 关闭保存布局、重开恢复（右栏宽 + tab 选中）。
/// 用真实 <see cref="AppShellViewModel"/> 走完整 SourceInitialized/Closing
/// 生命周期 —— <c>RestoreLayout</c>/<c>SaveLayout</c> 只有在
/// <c>DataContext is AppShellViewModel</c> 时才生效，因此测试必须构造真实 VM
/// （镜像 <c>UdsWindowTests.NewVm</c> 的依赖清单），不能用一个裸 shim。
/// </summary>
/// <remarks>
/// STA 约束：AppShell 是 WPF Window，ctor 要求 STA；AppShell.xaml 又引用
/// <see cref="System.Windows.Application"/> 资源中的 {StaticResource} 令牌
/// （Accent/CanvasBg/...），所以每个 STA body 都包在
/// <see cref="LeakedApplicationReset.RunWithTokenResources(Action)"/> 里 ——
/// 它新建 Application 并合并生产 Colors.xaml 令牌字典，测试后清理静态单例。
/// </remarks>
[Collection(WpfAppTestCollection.Name)]
public class AppShellLayoutPersistenceTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static LayoutStateStore MakeStore() => new(
        NullLogger<LayoutStateStore>.Instance,
        Path.Combine(Path.GetTempPath(), $"appshell-layout-{Guid.NewGuid():N}.json"));

    /// <summary>
    /// Hand-rolled <see cref="DbcService"/> stub — the shell only navigates
    /// between tabs; it never loads a real DBC. Keeps the test hermetic.
    /// </summary>
    private sealed class FakeDbcService : DbcService
    {
        public FakeDbcService() : base(NullLogger<DbcService>.Instance) { }
        public override Task LoadAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Build a real <see cref="AppShellViewModel"/> with the same dependency
    /// surface the production DI uses. Mirrors <c>UdsWindowTests.NewVm</c> —
    /// <c>RestoreLayout</c>/<c>SaveLayout</c>/<c>TestSetLayout</c> all require
    /// <c>DataContext is AppShellViewModel</c>, so a bare shim would silently
    /// skip the persistence path.
    /// </summary>
    private static AppShellViewModel NewVm()
    {
        var isoTp = new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { });
        var udsClient = new UdsClient(isoTp);
        var recentTemp = Path.Combine(
            Path.GetTempPath(),
            $"recent-uds-{Guid.NewGuid():N}.json");
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
            new RecordViewModel(new RecordService(NullLogger<RecordService>.Instance), NullLogger<RecordViewModel>.Instance),
            new ReplayViewModel(
                Substitute.For<IReplayService>(),
                Substitute.For<IFileDialogService>(),
                Substitute.For<IAscContentHasher>(),
                Substitute.For<IAscLocator>(),
                new TraceSessionLibrary(Path.Combine(Path.GetTempPath(), $"uds-tmtrace-{Guid.NewGuid():N}.tmtrace"), NullLogger<TraceSessionLibrary>.Instance),
                new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, recentTemp)),
            new MultiFrameSendViewModel(new SequenceSendService(new SendService(NullLogger<SendService>.Instance))),
            Substitute.For<ITraceSessionService>(),
            () => Substitute.For<TraceViewerViewModel>(),
            new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, recentTemp),
            Substitute.For<IFileDialogService>(),
            Substitute.For<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>(),
            new HilViewModel(Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(), Substitute.For<IHilReportService>()),
            new EcuScriptEditorViewModel(Substitute.For<IFileDialogService>(), Substitute.For<PeakCan.Host.App.Services.Trace.IMessageBoxPrompt>(), NullLogger<EcuScriptEditorViewModel>.Instance));
    }

    private sealed class FakeChannelFactory : PeakCan.HIL.Core.IChannelFactory
    {
        public ICanChannel Create(ChannelId id) => new FakeCanChannel(id);
    }

    private sealed class FakeCanChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; } = true;
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
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

    private sealed class FakeChannelProbe : PeakCan.HIL.Core.IChannelProbe
    {
        public ProbeResult Probe(ushort handle)
            => new(true, $"fake probe ok 0x{handle:X2}");
    }

    /// <summary>
    /// Run <paramref name="body"/> on an STA thread with a fresh tokenized
    /// WPF Application. Mirrors <c>UdsWindowTests.RunSta</c>: the tokenized
    /// AppShell resolves {StaticResource} from the production Colors.xaml
    /// merged via <see cref="LeakedApplicationReset.RunWithTokenResources(Action)"/>,
    /// and the leaked Application singleton is cleaned around the thread.
    /// </summary>
    private static void RunSta(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            LeakedApplicationReset.RunWithTokenResources(body);
            return;
        }
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { LeakedApplicationReset.RunWithTokenResources(body); }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        LeakedApplicationReset.CleanupLeakedApplication();
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        LeakedApplicationReset.CleanupLeakedApplication();
        if (thread.IsAlive)
        {
            throw new TimeoutException("STA thread did not complete within 30 s — likely a WPF dispatcher deadlock");
        }
        if (caught is not null) throw caught;
    }

    [Fact]
    public void Closing_Saves_And_Reopen_Restores_Layout()
    {
        var store = MakeStore();

        // 第一次生命周期：真实 VM + 人为改布局（拖 splitter / 切 tab）→ Close
        // 触发 OnClosing → SaveLayout → store.Set(...)（右栏宽 + 主右 tab）。
        RunSta(() =>
        {
            var shell = new AppShell
            {
                WindowStateStore = null,
                LayoutStateStore = store,
                DataContext = NewVm(),
            };
            Application.Current!.MainWindow = shell;
            shell.Show();
            Pump();
            shell.TestSetLayout(420.0, 2, 1);
            Pump();
            shell.Close();
        });

        // Closing 必须把当前布局写进 store。
        store.Get().Should().NotBeNull("Closing 必须保存布局");
        store.Get()!.RightPanelWidth.Should().BeApproximately(420.0, 0.01);
        store.Get()!.SelectedMainTabIndex.Should().Be(2);
        store.Get()!.SelectedRightTabIndex.Should().Be(1);

        // 第二次生命周期（重开）：同一 store 构造新 shell → OnSourceInitialized →
        // RestoreLayout（在 ShowTrace 之后执行）把右栏宽 + 主/右 tab 全部恢复。
        RunSta(() =>
        {
            var shell = new AppShell
            {
                WindowStateStore = null,
                LayoutStateStore = store,
                DataContext = NewVm(),
            };
            Application.Current!.MainWindow = shell;
            shell.Show();
            Pump();

            shell.RightPanelColumn.Width.Value.Should().BeApproximately(420.0, 0.01,
                "RestoreLayout 必须把右栏宽恢复为上次保存的值");
            var vm = (AppShellViewModel)shell.DataContext!;
            vm.SelectedMainTabIndex.Should().Be(2,
                "RestoreLayout 在 ShowTrace 之后执行 —— 保存的主区域 tab 必须胜出");
            vm.SelectedRightTabIndex.Should().Be(1,
                "RestoreLayout 必须恢复右侧常驻面板的选中 tab");
            shell.Close();
        });
    }
}
