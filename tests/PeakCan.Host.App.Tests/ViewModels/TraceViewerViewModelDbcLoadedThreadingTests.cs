using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 回归（2026-11-20 生产日志 peak-20261120.log）：
/// <c>DbcService.LoadAsync</c> 在线程池线程上触发 <c>DbcLoaded</c>
/// （内部全程 <c>ConfigureAwait(false)</c>，见 LoadLifecycle.partial.cs）。
/// <see cref="TraceViewerViewModel.OnDbcLoaded"/> 早期直接在该工作线程上
/// 改 <c>WatchedSignals</c>（ObservableCollection）→ 触发
/// <c>RefreshSamplingTable</c> 的 <c>SamplingRows.Clear()</c> → WPF
/// CollectionView 抛 NotSupportedException（"该类型的 CollectionView
/// 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改"）；
/// 该异常再被 DbcService 的兜底 catch 误报成 "DBC IO failed" + LoadFailed。
/// <para>
/// 本测试故意走真实 <c>LoadAsync</c> + 真实 DBC 临时文件，并用
/// <c>CollectionViewSource.GetDefaultView(SamplingRows)</c> 在生产里
/// DataGrid 绑定所创建的同一个默认视图上武装线程检查 —— MTA 测试
/// （无 Application，RunOnUi 走 inline）无法复现这条路径。
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class TraceViewerViewModelDbcLoadedThreadingTests
{
    public TraceViewerViewModelDbcLoadedThreadingTests()
        => LeakedApplicationReset.CleanupLeakedApplication();

    private static ITraceSessionRegistry MakeFakeRegistry()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        return registry;
    }

    // v3.x（会话状态剥离 Task 3）：ITraceSessionService 替身——WatchedSignals
    // 必须是非空集合，否则 VM ctor 对 WatchedSignals.CollectionChanged 的订阅会 NRE。
    private static ITraceSessionService MakeFakeSession()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        return session;
    }

    private static TraceViewerViewModel NewVm(DbcService dbc)
        => new(
            MakeFakeSession(),
            MakeFakeRegistry(),
            dbc,
            Substitute.For<ILogger<TraceViewerViewModel>>(),
            new TraceSessionLibrary(
                Path.Combine(Path.GetTempPath(), $"tmtrace-vm-dbcthread-{Guid.NewGuid():N}.tmtrace"),
                NullLogger<TraceSessionLibrary>.Instance));

    private static string WriteTempDbc()
    {
        // 最小合法 DBC（与 DbcServiceTests 同款）：一条无信号 BO_。
        var path = Path.Combine(Path.GetTempPath(), $"peakcan-dbc-thread-{Guid.NewGuid():N}.dbc");
        File.WriteAllText(path,
            "VERSION \"\"\n" +
            "\n" +
            "NS_ :\n" +
            "\n" +
            "BS_ :\n" +
            "\n" +
            "BU_: ECU1\n" +
            "\n" +
            "BO_ 256 Msg: 8 ECU1\n");
        return path;
    }

    [Fact]
    public void OnDbcLoaded_From_Worker_Thread_Marshals_Ui_Mutation_And_No_Spurious_LoadFailed()
    {
        Exception? caught = null;
        Error? spuriousLoadFailed = null;
        var uiThreadId = 0;
        var watchedSignalsMutationThreadId = 0;
        var placeholderRows = -1;

        // 防泄漏：并行集合里其它 STA 测试可能留下 Application.Current 单例。
        LeakedApplicationReset.CleanupLeakedApplication();
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                uiThreadId = Environment.CurrentManagedThreadId;

                var dbc = new DbcService(NullLogger<DbcService>.Instance);
                var sut = NewVm(dbc);
                dbc.LoadFailed += e => spuriousLoadFailed = e;
                sut.WatchedSignals.CollectionChanged += (_, _) =>
                    watchedSignalsMutationThreadId = Environment.CurrentManagedThreadId;

                // 武装 CollectionView 的跨线程检查——生产里 DataGrid 绑定
                // SamplingRows 时创建的就是这个默认视图。
                CollectionViewSource.GetDefaultView(sut.SamplingRows).Should().NotBeNull();

                var path = WriteTempDbc();
                try
                {
                    var load = dbc.LoadAsync(path);

                    // 修复后 OnDbcLoaded 会从线程池 Invoke 回 UI 线程；
                    // 必须泵消息，否则同步 Invoke 死锁（也即生产里 UI 线程
                    // 本就一直在泵消息）。
                    var frame = new DispatcherFrame();
                    load.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                    load.GetAwaiter().GetResult();
                }
                finally
                {
                    if (File.Exists(path)) File.Delete(path);
                }

                placeholderRows = sut.WatchedSignals.Count(w => w.IsPlaceholder);
            }
            catch (Exception ex) { caught = ex; }
            finally
            {
                try { Application.Current?.Shutdown(); } catch { /* dispatcher may already be shutting down */ }
                LeakedApplicationReset.CleanupLeakedApplication();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "STA thread must complete within 30 s — a hang means the DbcLoaded handler deadlocked the dispatcher");

        if (caught is not null) throw caught;
        spuriousLoadFailed.Should().BeNull(
            "DBC 解析成功；handler 的跨线程异常不得被 DbcService 兜底 catch 误报为加载失败");
        watchedSignalsMutationThreadId.Should().Be(uiThreadId,
            "WatchedSignals 的变更必须封送到 UI 线程（WPF CollectionView 线程亲和）");
        placeholderRows.Should().Be(1, "DBC 加载后 watch 列表应有一条占位行");
    }
}
