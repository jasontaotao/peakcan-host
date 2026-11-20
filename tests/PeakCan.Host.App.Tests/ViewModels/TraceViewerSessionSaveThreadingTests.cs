using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Services;
using ScottPlot;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 回归（用户报告：保存会话窗口卡住死掉）：
/// <c>SaveSessionAsync</c> 曾在 UI 线程上调用同步 <c>BuildSnapshot()</c>，
/// 而 <c>BuildSnapshotAsync</c> 的哈希 <c>await ... ConfigureAwait(true)</c>
/// 需要回到同一个 Dispatcher。UI 线程卡在 <c>GetResult()</c> 上，
/// 续体永远排不进来 → 自锁。本测试用真实 Dispatcher + 真正异步的
/// hasher 复现该上下文，并验证保存不再阻塞 UI、同步入口改为 fail-fast。
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class TraceViewerSessionSaveThreadingTests
{
    public TraceViewerSessionSaveThreadingTests()
        => LeakedApplicationReset.CleanupLeakedApplication();

    [Fact]
    public void SaveSessionAsync_OnDispatcherThread_CompletesAndWritesBundle()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            var asc = Path.Combine(Path.GetTempPath(), $"session-save-{Guid.NewGuid():N}.asc");
            var target = Path.Combine(Path.GetTempPath(), $"session-save-{Guid.NewGuid():N}.tmtrace");
            try
            {
                LeakedApplicationReset.CleanupLeakedApplication();
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                File.WriteAllText(asc, "synthetic asc");
                var vm = NewVm(asc, target);

                // 修复后保存立即返回 Task；泵消息让哈希续体回到 UI 线程。
                var save = vm.SaveSessionAsync(target);
                var frame = new DispatcherFrame();
                save.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                save.GetAwaiter().GetResult();

                File.Exists(target).Should().BeTrue("保存成功后必须落盘");
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                try { if (File.Exists(asc)) File.Delete(asc); } catch { /* best effort */ }
                try { if (File.Exists(target)) File.Delete(target); } catch { /* best effort */ }
                try { Application.Current?.Shutdown(); }
                catch { /* dispatcher may already be shutting down */ }
                LeakedApplicationReset.CleanupLeakedApplication();
            }
        })
        {
            // 若回归导致 UI 线程自锁，后台线程不能拖住测试进程退出。
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue(
            "SaveSessionAsync 不得在 UI 线程上自锁（旧实现卡在 BuildSnapshot→GetResult）");

        if (caught is not null) throw caught;
    }

    [Fact]
    public void BuildSnapshot_OnBackgroundThread_IsAllowed()
    {
        // 自动保存/测试线程没有 Dispatcher 上下文，同步入口必须照常可用；
        // 否则守卫会把后台路径误伤成异常。
        var vm = NewVm(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.asc"),
            Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.tmtrace"));

        var dto = vm.BuildSnapshot();

        dto.Should().NotBeNull();
    }

    [Fact]
    public void BuildSnapshot_OnDispatcherThread_ThrowsInsteadOfDeadlocking()
    {
        Exception? caught = null;
        var guardThrew = false;

        var thread = new Thread(() =>
        {
            try
            {
                LeakedApplicationReset.CleanupLeakedApplication();
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                var vm = NewVm(
                    Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.asc"),
                    Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.tmtrace"));

                try
                {
                    vm.BuildSnapshot();
                }
                catch (InvalidOperationException)
                {
                    guardThrew = true;
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                try { Application.Current?.Shutdown(); }
                catch { /* dispatcher may already be shutting down */ }
                LeakedApplicationReset.CleanupLeakedApplication();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue();

        if (caught is not null) throw caught;
        guardThrew.Should().BeTrue("UI 线程调用同步快照必须 fail-fast，而不是卡死窗口");
    }

    private static TraceViewerViewModel NewVm(string ascPath, string libraryPath)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("src", "drive", ascPath, Colors.Blue, new LineStyle()),
        });

        return new TraceViewerViewModel(
            session,
            registry,
            Substitute.For<DbcService>(NullLogger<DbcService>.Instance),
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libraryPath, NullLogger<TraceSessionLibrary>.Instance),
            hasher: new YieldingHasher());
    }

    /// <summary>真正异步完成的 hasher：模拟大 .asc 的 SHA-256 让出。</summary>
    private sealed class YieldingHasher : IAscContentHasher
    {
        public Task<string> ComputeAsync(string path, CancellationToken ct = default)
            => Task.Run(async () =>
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                return new string('a', 64);
            }, ct);
    }
}
