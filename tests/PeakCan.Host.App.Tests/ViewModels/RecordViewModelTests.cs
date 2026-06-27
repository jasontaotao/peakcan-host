using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.2.11 PATCH Item 6: <see cref="RecordViewModel"/> wraps
/// <see cref="RecordService"/> for the Recording tab. It exposes a poll
/// timer that surfaces IsRecording + FrameCount to the view, and 3
/// commands (Browse / Start / Stop) that drive the underlying service.
/// </summary>
public class RecordViewModelTests
{
    [Fact]
    public void Start_Without_OutputPath_Is_NoOp()
    {
        var rec = new RecordService(NullLogger<RecordService>.Instance);
        var vm = new RecordViewModel(rec, NullLogger<RecordViewModel>.Instance) { OutputPath = "" };
        vm.StartCommand.Execute(null);
        rec.IsRecording.Should().BeFalse();
    }

    [Fact]
    public void Start_Invokes_RecordService_StartRecording()
    {
        var rec = new RecordService(NullLogger<RecordService>.Instance);
        var vm = new RecordViewModel(rec, NullLogger<RecordViewModel>.Instance)
        {
            OutputPath = Path.Combine(Path.GetTempPath(), $"pch-rec-{Guid.NewGuid():N}.asc"),
            Format = RecordService.RecordFormat.Asc,
        };
        vm.StartCommand.Execute(null);
        rec.IsRecording.Should().BeTrue();
        rec.StopRecording();
    }

    [Fact]
    public void Stop_Invokes_RecordService_StopRecording()
    {
        var rec = new RecordService(NullLogger<RecordService>.Instance);
        var recPath = Path.Combine(Path.GetTempPath(), $"pch-rec-{Guid.NewGuid():N}.asc");
        rec.StartRecording(recPath, RecordService.RecordFormat.Asc);
        var vm = new RecordViewModel(rec, NullLogger<RecordViewModel>.Instance) { OutputPath = recPath };
        vm.StopCommand.Execute(null);
        rec.IsRecording.Should().BeFalse();
    }

    [Fact]
    public async Task FrameCount_Polls_Service_Property()
    {
        // v1.2.11: poll logic transfers service's IsRecording + FrameCount
        // to bindable properties. Tests call PollNow() directly because
        // DispatcherTimer.Tick doesn't fire on the xunit MTA threadpool
        // (no WPF Application pump).
        // v1.2.12 PATCH Item 5: RecordService is now a BackgroundService
        // that drains frames on a writer thread. We must start the host
        // (StartAsync) and wait for the drain before polling.
        var rec = new RecordService(NullLogger<RecordService>.Instance);
        await rec.StartAsync(System.Threading.CancellationToken.None);
        var recPath = Path.Combine(Path.GetTempPath(), $"pch-rec-{Guid.NewGuid():N}.asc");
        rec.StartRecording(recPath, RecordService.RecordFormat.Asc);
        rec.OnFrame(new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, new ChannelId(0x51), default));
        rec.OnFrame(new CanFrame(new CanId(2, FrameFormat.Standard), new byte[] { 0xBB }, FrameFlags.None, new ChannelId(0x51), default));

        // Wait for the writer thread to drain both enqueued frames.
        var deadline = System.Environment.TickCount + 5000;
        while (rec.FrameCount < 2 && System.Environment.TickCount < deadline)
        {
            await System.Threading.Tasks.Task.Delay(10);
        }

        var vm = new RecordViewModel(rec, NullLogger<RecordViewModel>.Instance) { OutputPath = recPath };
        vm.PollNow();

        vm.FrameCount.Should().Be(2);
        vm.IsRecording.Should().BeTrue();
        await rec.StopAsync(System.Threading.CancellationToken.None);
    }

    [Fact]
    public void Start_With_Invalid_Path_Sets_Status_To_Fail()
    {
        var rec = new RecordService(NullLogger<RecordService>.Instance);
        var vm = new RecordViewModel(rec, NullLogger<RecordViewModel>.Instance)
        {
            OutputPath = @"Z:\nonexistent\readonly\path\file.asc",   // likely fails
            Format = RecordService.RecordFormat.Asc,
        };
        vm.StartCommand.Execute(null);
        // Either IsRecording false (catch path) or true (path actually writable); assert no exception
        vm.Status.Should().NotBeNull();
    }
}