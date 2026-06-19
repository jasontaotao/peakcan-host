using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.App.ViewModels;
using System.Windows;
using System.Windows.Threading;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 13: behavior of the TraceViewModel's ObservableCollection of TraceEntry
/// rows. The view model is intentionally parameterless so DI can instantiate
/// it without dragging in WPF types.
/// <para>
/// <b>Dispatcher contract:</b> <see cref="TraceViewModel.AppendBatchAsync"/>
/// silently no-ops when <c>Application.Current</c> is null (test contexts
/// that run on the xunit MTA threadpool). The STA-hosted test
/// <c>AppendBatch_On_StaThread_With_Application_Adds_All_Frames</c> exercises
/// the production dispatcher path. In the running app, <c>Application.Current.Dispatcher</c>
/// is always available, so the silent path is never hit at runtime.
/// </para>
/// </summary>
public class TraceViewModelTests
{
    private static CanFrame MakeFrame(uint id = 0x123, byte dlc = 4, bool fd = false, bool error = false)
    {
        byte[] payload = dlc == 0 ? Array.Empty<byte>() : new byte[dlc];
        var flags = FrameFlags.None;
        if (fd) flags |= FrameFlags.Fd;
        if (error) flags |= FrameFlags.ErrFrame;
        return new CanFrame(
            new CanId(id, FrameFormat.Standard),
            payload,
            flags,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL));
    }

    [Fact]
    public void Default_Entries_Is_Empty()
    {
        var vm = new TraceViewModel();
        vm.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Default_MaxRows_Is_Ten_Thousand()
    {
        // The plan specifies 10_000 as the FIFO trim threshold.
        var vm = new TraceViewModel();
        vm.MaxRows.Should().Be(10_000);
    }

    [Fact]
    public async Task AppendBatch_With_Null_Application_Returns_CompletedTask_Without_Adding()
    {
        // Documents the test-context limitation: xunit runs tests on the
        // MTA threadpool with no WPF Application, so Application.Current is
        // null and AppendBatchAsync returns CompletedTask without mutating
        // Entries. The production dispatcher path is exercised by the
        // STA-hosted test below.
        var vm = new TraceViewModel();
        var frames = new List<CanFrame>
        {
            MakeFrame(id: 0x111, dlc: 2),
            MakeFrame(id: 0x222, dlc: 4),
            MakeFrame(id: 0x333, dlc: 8, fd: true),
        };
        var task = vm.AppendBatchAsync(frames);
        await task;
        task.IsCompletedSuccessfully.Should().BeTrue();
        vm.Entries.Should().BeEmpty("xunit MTA thread has no WPF Application; dispatcher path is null");
    }

    [Fact]
    public void AppendBatch_On_StaThread_With_Application_Adds_All_Frames()
    {
        // The only test that exercises the production dispatcher path.
        // Spawns an STA thread, creates a WPF Application on it (so
        // Application.Current is non-null), invokes AppendBatchAsync, and
        // pumps the dispatcher with DispatcherFrame until the awaited
        // Task completes. Asserts all 3 frames land in Entries.
        Exception? caught = null;
        int entriesCount = 0;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var vm = new TraceViewModel();
                var frames = new List<CanFrame>
                {
                    MakeFrame(id: 0x111, dlc: 2),
                    MakeFrame(id: 0x222, dlc: 4),
                    MakeFrame(id: 0x333, dlc: 8, fd: true),
                };

                // AppendBatchAsync queues work onto the current dispatcher
                // (Application.Current.Dispatcher === Dispatcher.CurrentDispatcher
                // on this STA thread). Pump the dispatcher until the task
                // completes — using a DispatcherFrame so we don't deadlock.
                var task = vm.AppendBatchAsync(frames);
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);

                entriesCount = vm.Entries.Count;
            }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue("STA thread must complete within 5 s");

        if (caught is not null) throw caught;
        entriesCount.Should().Be(3, "the dispatcher path should add all 3 frames");
    }
}
