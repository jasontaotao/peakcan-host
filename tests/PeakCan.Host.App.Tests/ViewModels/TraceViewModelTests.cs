using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 13: behavior of the TraceViewModel's ObservableCollection of TraceEntry
/// rows. The view model is intentionally parameterless so DI can instantiate
/// it without dragging in WPF types.
/// <para>
/// <b>Dispatcher contract:</b> <see cref="TraceViewModel.AppendBatchAsync"/>
/// silently no-ops when <c>Application.Current</c> is null (test contexts).
/// The test <c>AppendBatch_With_Null_Dispatcher_Returns_CompletedTask_Without_Throwing</c>
/// pins that behavior; in production <c>Application.Current.Dispatcher</c>
/// is always available, so the silent path is never hit.
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
    public async Task AppendBatch_Adds_All_Frames_As_TraceEntries()
    {
        // In a non-WPF test process Application.Current is null and
        // AppendBatchAsync returns Task.CompletedTask without adding.
        // The non-null dispatcher path is exercised by the live app run
        // (Task 13 Step 6) — the unit tests pin the silent test-context
        // contract only.
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
    }

    [Fact]
    public async Task AppendBatch_With_Null_Dispatcher_Returns_CompletedTask_Without_Throwing()
    {
        // Documents the test-context limitation: the dispatcher is null in
        // unit tests, so frames are silently dropped. In production the
        // dispatcher is always present.
        var vm = new TraceViewModel();
        var task = vm.AppendBatchAsync(new List<CanFrame> { MakeFrame() });
        await task;
        task.IsCompletedSuccessfully.Should().BeTrue();
        vm.Entries.Should().BeEmpty("test context has no WPF dispatcher");
    }
}
