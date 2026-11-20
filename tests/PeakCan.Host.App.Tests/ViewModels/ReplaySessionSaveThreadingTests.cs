using System.IO;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Replay 会话快照的线程回归：
/// <list type="bullet">
/// <item>BuildSnapshotAsync 在第一个 await 之前捕获 UI 状态，之后
/// 不得再向调用方 SynchronizationContext 投递续体——否则 OnExit
/// 关停 Dispatcher 后自动保存永远不完成。</item>
/// <item>UI 线程上的同步入口必须 fail-fast；后台线程仍允许。</item>
/// </list>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class ReplaySessionSaveThreadingTests
{
    public ReplaySessionSaveThreadingTests()
        => LeakedApplicationReset.CleanupLeakedApplication();

    [Fact]
    public async Task BuildSnapshotAsync_OnContextThatNeverPumps_CompletesOnThreadPool()
    {
        var ascPath = Path.Combine(Path.GetTempPath(), $"replay-snapshot-{Guid.NewGuid():N}.asc");
        File.WriteAllText(ascPath, "synthetic asc");
        try
        {
            using var vm = NewVm(new YieldingHasher());
            vm.LoadedFilePath = ascPath;

            var context = new RecordingSynchronizationContext();
            Task<TraceSessionBundleDto>? task = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                task = vm.BuildSnapshotAsync();
            })
            {
                IsBackground = true,
            };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(
                "BuildSnapshotAsync 应在第一个 await 处立即让出，不能同步哈希");

            task.Should().NotBeNull();
            // 用 await + 超时而不是 Wait/Result：捕获到 RecordingContext 的续体
            // 永远不执行，这里会以 TimeoutException 失败而不是挂住测试。
            var dto = await task!.WaitAsync(TimeSpan.FromSeconds(10));
            context.PostCount.Should().Be(0,
                "UI 状态已在 await 前捕获，续体不应再投递到 SynchronizationContext");
            dto.Sources.Should().ContainSingle()
                .Which.ContentHash.Should().Be(new string('a', 64));
        }
        finally
        {
            try { if (File.Exists(ascPath)) File.Delete(ascPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void BuildSnapshot_OnDispatcherThread_ThrowsInsteadOfBlocking()
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

                using var vm = NewVm(new SyncHasher());
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
        guardThrew.Should().BeTrue("Replay 的 UI 线程同步入口必须 fail-fast");
    }

    [Fact]
    public void BuildSnapshot_OnBackgroundThread_IsAllowed()
    {
        // 自动保存/测试线程没有 Dispatcher 上下文，同步入口必须照常可用；
        // 否则守卫会把后台路径误伤成异常。
        using var vm = NewVm(new SyncHasher());

        var dto = vm.BuildSnapshot();

        dto.Should().NotBeNull();
    }

    private static ReplayViewModel NewVm(IAscContentHasher hasher)
    {
        var service = Substitute.For<IReplayService>();
        service.TotalDuration.Returns(10.0);
        var library = new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-replay-thread-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var recent = new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-replay-thread-{Guid.NewGuid():N}.json"));
        return new ReplayViewModel(
            service,
            Substitute.For<IFileDialogService>(),
            hasher,
            Substitute.For<IAscLocator>(),
            library,
            recent);
    }

    /// <summary>真正异步完成：模拟大文件 SHA-256 让出。</summary>
    private sealed class YieldingHasher : IAscContentHasher
    {
        public Task<string> ComputeAsync(string path, CancellationToken ct = default)
            => Task.Run(async () =>
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                return new string('a', 64);
            }, ct);
    }

    private sealed class SyncHasher : IAscContentHasher
    {
        public Task<string> ComputeAsync(string path, CancellationToken ct = default)
            => Task.FromResult("");
    }

    /// <summary>记录投递次数但永不执行的上下文：捕获到它的续体将永远挂起。</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
            => Interlocked.Increment(ref _postCount);

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException();
    }
}
